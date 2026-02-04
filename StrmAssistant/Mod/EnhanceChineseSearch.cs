using HarmonyLib;
using MediaBrowser.Controller.Entities;
using SQLitePCL.pretty;
using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using static StrmAssistant.Mod.PatchManager;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Mod
{
    public class EnhanceChineseSearch : PatchBase<EnhanceChineseSearch>
    {
        private static readonly Version AppVer = Plugin.Instance.ApplicationHost.ApplicationVersion;
        private static readonly Version Ver4830 = new Version("4.8.3.0");
        private static readonly Version Ver4900 = new Version("4.9.0.0");
        private static readonly Version Ver4937 = new Version("4.9.0.37");

        private static Type raw;
        private static MethodInfo sqlite3_enable_load_extension;
        private static FieldInfo sqlite3_db;
        private static MethodInfo _createConnection;
        private static PropertyInfo _dbFilePath;
        private static MethodInfo _getJoinCommandText;
        private static MethodInfo _createSearchTerm;
        private static MethodInfo _cacheIdsFromTextParams;
        private static bool _getJoinCommandTextReturnsStringBuilder;

        public static string CurrentTokenizerName { get; private set; } = "unknown";

        private static string _tokenizerPath;
        private static readonly object _lock = new object();
        private static bool _patchPhase2Initialized;
        private static readonly Dictionary<string, Regex> patterns = new Dictionary<string, Regex>
        {
            { "imdb", new Regex(@"^tt\d{7,8}$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "tmdb", new Regex(@"^tmdb(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) },
            { "tvdb", new Regex(@"^tvdb(id)?=(\d+)$", RegexOptions.IgnoreCase | RegexOptions.Compiled) }
        };

        public EnhanceChineseSearch()
        {
            _tokenizerPath = Path.Combine(Plugin.Instance.ApplicationPaths.PluginsPath, "libsimple.so");

            Initialize();

            if (Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.EnhanceChineseSearch ||
                Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.EnhanceChineseSearchRestore)
            {
                if (AppVer >= Ver4830)
                {
                    PatchPhase1();
                }
                else
                {
                    ResetOptions();
                }
            }
        }

        protected override void OnInitialize()
        {
            try
            {
                // 加载 SQLitePCLRawEx
                var sqlitePCLEx = EmbyVersionCompatibility.TryLoadAssembly("SQLitePCLRawEx.core");
                if (sqlitePCLEx != null)
                {
                    raw = EmbyVersionCompatibility.TryGetType(sqlitePCLEx, "SQLitePCLEx.raw");
                    if (raw != null)
                    {
                        sqlite3_enable_load_extension = raw.GetMethod("sqlite3_enable_load_extension",
                            BindingFlags.Static | BindingFlags.Public);
                    }
                }

                // 获取 SQLite 数据库连接字段
                sqlite3_db = typeof(SQLiteDatabaseConnection).GetField("db", BindingFlags.NonPublic | BindingFlags.Instance);

                // 加载 Emby.Sqlite
                var embySqlite = EmbyVersionCompatibility.TryLoadAssembly("Emby.Sqlite");
                if (embySqlite != null)
                {
                    var baseSqliteRepository = EmbyVersionCompatibility.TryGetType(embySqlite, "Emby.Sqlite.BaseSqliteRepository");
                    if (baseSqliteRepository != null)
                    {
                        // 尝试获取 CreateConnection 方法，处理可能的重载
                        var createConnectionMethods = baseSqliteRepository.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                            .Where(m => m.Name == "CreateConnection")
                            .ToArray();
                        
                        if (createConnectionMethods.Length == 1)
                        {
                            _createConnection = createConnectionMethods[0];
                        }
                        else if (createConnectionMethods.Length > 1)
                        {
                            // 如果有多个重载，选择无参数的版本
                            _createConnection = createConnectionMethods.FirstOrDefault(m => m.GetParameters().Length == 0)
                                ?? createConnectionMethods.FirstOrDefault(m => m.GetParameters().Length == 1);
                            
                            if (Plugin.Instance.DebugMode && _createConnection != null)
                            {
                                Plugin.Instance.Logger.Debug($"EnhanceChineseSearch: Selected CreateConnection with {_createConnection.GetParameters().Length} parameters");
                            }
                        }
                        
                        _dbFilePath = baseSqliteRepository.GetProperty("DbFilePath", 
                            BindingFlags.NonPublic | BindingFlags.Instance);
                    }
                }

                // 加载 Emby.Server.Implementations
                var embyServerImplementationsAssembly = EmbyVersionCompatibility.TryLoadAssembly("Emby.Server.Implementations");
                if (embyServerImplementationsAssembly != null)
                {
                    var sqliteItemRepository = EmbyVersionCompatibility.TryGetType(
                        embyServerImplementationsAssembly, 
                        "Emby.Server.Implementations.Data.SqliteItemRepository");
                    
                    if (sqliteItemRepository != null)
                    {
                        // 处理 GetJoinCommandText 可能的重载
                        var sqliteItemRepositoryType = sqliteItemRepository;
                        _getJoinCommandText = EmbyVersionCompatibility.FindCompatibleMethod(
                            sqliteItemRepositoryType, 
                            "GetJoinCommandText", 
                            BindingFlags.NonPublic | BindingFlags.Instance,
                            // Emby 4.9.1.x 及以上版本，移除了 itemLinks2TableQualifier 参数
                            new[] { typeof(MediaBrowser.Controller.Entities.InternalItemsQuery), typeof(List<KeyValuePair<string, string>>), typeof(string), typeof(bool) },
                            // Emby 4.9.0.x 版本
                            new[] { typeof(MediaBrowser.Controller.Entities.InternalItemsQuery), typeof(List<KeyValuePair<string, string>>), typeof(string), typeof(string), typeof(bool) }
                        );

                        if (_getJoinCommandText != null)
                        {
                            if (Plugin.Instance.DebugMode)
                            {
                                var paramCount = _getJoinCommandText.GetParameters().Length;
                                Plugin.Instance.Logger.Debug($"EnhanceChineseSearch: Selected GetJoinCommandText with {paramCount} parameters");
                            }

                            // 检查返回类型
                            var returnType = _getJoinCommandText.ReturnType;
                            _getJoinCommandTextReturnsStringBuilder = returnType.Name == "StringBuilder" ||
                                returnType.FullName == "System.Text.StringBuilder";
                            
                            if (Plugin.Instance.DebugMode)
                            {
                                Plugin.Instance.Logger.Debug($"EnhanceChineseSearch: GetJoinCommandText returns {returnType.Name}");
                            }
                        }
                        
                        _createSearchTerm = sqliteItemRepository.GetMethod("CreateSearchTerm", 
                            BindingFlags.NonPublic | BindingFlags.Static);
                        _cacheIdsFromTextParams = sqliteItemRepository.GetMethod("CacheIdsFromTextParams",
                            BindingFlags.Instance | BindingFlags.NonPublic);
                    }
                }

                // 验证所有必需的组件
                var missingComponents = new List<string>();
                if (raw == null) missingComponents.Add("SQLitePCLEx.raw");
                if (sqlite3_enable_load_extension == null) missingComponents.Add("sqlite3_enable_load_extension");
                if (sqlite3_db == null) missingComponents.Add("SQLiteDatabaseConnection.db");
                if (_createConnection == null) missingComponents.Add("CreateConnection");
                if (_dbFilePath == null) missingComponents.Add("DbFilePath");
                if (_getJoinCommandText == null) missingComponents.Add("GetJoinCommandText");
                if (_createSearchTerm == null) missingComponents.Add("CreateSearchTerm");
                if (_cacheIdsFromTextParams == null) missingComponents.Add("CacheIdsFromTextParams");

                if (missingComponents.Any())
                {
                    Plugin.Instance.Logger.Warn($"EnhanceChineseSearch: Missing components - {string.Join(", ", missingComponents)}");
                    Plugin.Instance.Logger.Warn($"Chinese search enhancement may not work on Emby {AppVer}");
                    Plugin.Instance.Logger.Info("This feature requires internal SQLite APIs that may have changed in this Emby version");
                    
                    // 标记为不可用
                    PatchTracker.FallbackPatchApproach = PatchApproach.None;
                    
                    EmbyVersionCompatibility.LogCompatibilityInfo(
                        nameof(EnhanceChineseSearch),
                        false,
                        $"{missingComponents.Count} required components not found");
                }
                else
                {
                    Plugin.Instance.Logger.Info("EnhanceChineseSearch: All components loaded successfully");
                    Plugin.Instance.Logger.Info($"Chinese search enhancement is compatible with Emby {AppVer}");
                    
                    EmbyVersionCompatibility.LogCompatibilityInfo(
                        nameof(EnhanceChineseSearch),
                        true,
                        "All required SQLite components available");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Error($"EnhanceChineseSearch initialization failed: {ex.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"Exception type: {ex.GetType().Name}");
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
                
                EmbyVersionCompatibility.LogCompatibilityInfo(
                    nameof(EnhanceChineseSearch),
                    false,
                    "Initialization error - feature disabled");
            }
        }

        protected override void Prepare(bool apply)
        {
            // No action needed
        }

        private static void PatchPhase1()
        {
            if (EnsureTokenizerExists() && PatchUnpatch(Instance.PatchTracker, true, _createConnection,
                    postfix: nameof(CreateConnectionPostfix))) return;

            if (Plugin.Instance.DebugMode)
            {
                Plugin.Instance.Logger.Debug("EnhanceChineseSearch - PatchPhase1 Failed");
            }
            
            ResetOptions();
        }

        private static void PatchPhase2(IDatabaseConnection connection)
        {
            string ftsTableName;

            if (AppVer >= Ver4830)
            {
                ftsTableName = "fts_search9";
            }
            else
            {
                ftsTableName = "fts_search8";
            }

            var tokenizerCheckQuery = $@"
                SELECT 
                    CASE 
                        WHEN instr(sql, 'tokenize=""simple""') > 0 THEN 'simple'
                        WHEN instr(sql, 'tokenize=""unicode61 remove_diacritics 2""') > 0 THEN 'unicode61 remove_diacritics 2'
                        ELSE 'unknown'
                    END AS tokenizer_name
                FROM 
                    sqlite_master 
                WHERE 
                    type = 'table' AND 
                    name = '{ftsTableName}';";

            var rebuildFtsResult = true;
            var patchSearchFunctionsResult = false;

            try
            {
                using (var statement = connection.PrepareStatement(tokenizerCheckQuery))
                {
                    if (statement.MoveNext())
                    {
                        CurrentTokenizerName = statement.Current?.GetString(0) ?? "unknown";
                    }
                }

                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Current tokenizer (before) is " + CurrentTokenizerName);

                if (!string.Equals(CurrentTokenizerName, "unknown", StringComparison.Ordinal))
                {
                    if (Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.EnhanceChineseSearchRestore)
                    {
                        if (string.Equals(CurrentTokenizerName, "simple", StringComparison.Ordinal))
                        {
                            rebuildFtsResult = RebuildFts(connection, ftsTableName, "unicode61 remove_diacritics 2");
                        }
                        if (rebuildFtsResult)
                        {
                            CurrentTokenizerName = "unicode61 remove_diacritics 2";
                            Plugin.Instance.Logger.Info("EnhanceChineseSearch - Restore Success");
                        }
                        ResetOptions();
                    }
                    else if (Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.EnhanceChineseSearch)
                    {
                        patchSearchFunctionsResult = PatchSearchFunctions();

                        if (patchSearchFunctionsResult)
                        {
                            if (string.Equals(CurrentTokenizerName, "unicode61 remove_diacritics 2", StringComparison.Ordinal))
                            {
                                rebuildFtsResult = RebuildFts(connection, ftsTableName, "simple");
                            }

                            if (rebuildFtsResult)
                            {
                                CurrentTokenizerName = "simple";
                                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Load Success");
                            }
                        }
                    }
                }

                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Current tokenizer (after) is " + CurrentTokenizerName);
            }
            catch (Exception e)
            {
                // 记录错误信息，但只在 Debug 模式下记录详细堆栈
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - PatchPhase2 Failed: {e.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"Exception type: {e.GetType().Name}");
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                    if (e.InnerException != null)
                    {
                        Plugin.Instance.Logger.Debug($"Inner exception: {e.InnerException.GetType().Name}: {e.InnerException.Message}");
                    }
                }
            }

            // 只有在真正失败时才重置选项
            // 如果 patch 失败但 tokenizer 已经是 simple，说明之前已经成功启用过，不应该重置
            // 只有在 tokenizer 仍然是 unicode61 且 patch 失败时，才认为是真正的失败
            if (!patchSearchFunctionsResult)
            {
                if (string.Equals(CurrentTokenizerName, "unicode61 remove_diacritics 2", StringComparison.Ordinal))
                {
                    // Patch 失败且 tokenizer 未切换，说明功能无法启用
                    Plugin.Instance.Logger.Warn("EnhanceChineseSearch: Patch failed and tokenizer not switched, resetting options");
                    ResetOptions();
                }
                else if (string.Equals(CurrentTokenizerName, "simple", StringComparison.Ordinal))
                {
                    // Tokenizer 已经是 simple，说明之前已经成功启用过
                    // 即使 patch 失败（可能因为使用 Reflection），功能仍然可用
                    Plugin.Instance.Logger.Info("EnhanceChineseSearch: Patch failed but tokenizer is already 'simple', keeping options enabled");
                }
            }
            else if (!rebuildFtsResult || string.Equals(CurrentTokenizerName, "unknown", StringComparison.Ordinal))
            {
                // Patch 成功但重建 FTS 失败，或者 tokenizer 状态未知
                Plugin.Instance.Logger.Warn("EnhanceChineseSearch: Patch succeeded but FTS rebuild failed or tokenizer unknown, resetting options");
                ResetOptions();
            }
        }

        private static bool RebuildFts(IDatabaseConnection connection, string ftsTableName, string tokenizerName)
        {
            string populateQuery;

            if (AppVer < Ver4900)
            {
                populateQuery =
                    $"insert into {ftsTableName}(RowId, Name, OriginalTitle, SeriesName, Album) select id, " +
                    GetSearchColumnNormalization("Name") + ", " +
                    GetSearchColumnNormalization("OriginalTitle") + ", " +
                    GetSearchColumnNormalization("SeriesName") + ", " +
                    GetSearchColumnNormalization("Album") +
                    " from MediaItems";
            }
            else
            {
                populateQuery =
                    $"insert into {ftsTableName}(RowId, Name, OriginalTitle, SeriesName, Album) select id, " +
                    GetSearchColumnNormalization("Name") + ", " +
                    GetSearchColumnNormalization("OriginalTitle") + ", " +
                    GetSearchColumnNormalization("SeriesName") + ", " +
                    GetSearchColumnNormalization(
                        "(select case when AlbumId is null then null else (select name from MediaItems where Id = AlbumId limit 1) end)") +
                    " from MediaItems";
            }

            connection.BeginTransaction(TransactionMode.Deferred);
            try
            {
                var dropFtsTableQuery = $"DROP TABLE IF EXISTS {ftsTableName}";
                connection.Execute(dropFtsTableQuery);

                var createFtsTableQuery =
                    $"CREATE VIRTUAL TABLE IF NOT EXISTS {ftsTableName} USING FTS5 (Name, OriginalTitle, SeriesName, Album, tokenize=\"{tokenizerName}\", prefix='1 2 3 4')";
                connection.Execute(createFtsTableQuery);

                Plugin.Instance.Logger.Info($"EnhanceChineseSearch - Filling {ftsTableName} Start");

                connection.Execute(populateQuery);
                connection.CommitTransaction();

                Plugin.Instance.Logger.Info($"EnhanceChineseSearch - Filling {ftsTableName} Complete");

                return true;
            }
            catch (Exception e)
            {
                connection.RollbackTransaction();

                // 记录错误信息，但只在 Debug 模式下记录详细堆栈
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - RebuildFts Failed: {e.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"Exception type: {e.GetType().Name}");
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                    if (e.InnerException != null)
                    {
                        Plugin.Instance.Logger.Debug($"Inner exception: {e.InnerException.GetType().Name}: {e.InnerException.Message}");
                    }
                }
            }

            return false;
        }

        private static string GetSearchColumnNormalization(string columnName)
        {
            return "replace(replace(" + columnName + ",'''',''),'.','')";
        }

        private static bool EnsureTokenizerExists()
        {
            var resourceName = GetTokenizerResourceName();
            var expectedSha1 = GetExpectedSha1();

            if (resourceName == null || expectedSha1 == null) return false;

            try
            {
                if (File.Exists(_tokenizerPath))
                {
                    var existingSha1 = ComputeSha1(_tokenizerPath);

                    if (expectedSha1.ContainsValue(existingSha1))
                    {
                        var highestVersion = expectedSha1.Keys.Max();
                        var highestSha1 = expectedSha1[highestVersion];

                        if (existingSha1 == highestSha1)
                        {
                            Plugin.Instance.Logger.Info(
                                $"EnhanceChineseSearch - Tokenizer exists with matching SHA-1 for the highest version {highestVersion}");
                        }
                        else
                        {
                            var currentVersion = expectedSha1.FirstOrDefault(x => x.Value == existingSha1).Key;
                            Plugin.Instance.Logger.Info(
                                $"EnhanceChineseSearch - Tokenizer exists for version {currentVersion} but does not match the highest version {highestVersion}. Upgrading...");
                            ExportTokenizer(resourceName);
                        }

                        return true;
                    }

                    Plugin.Instance.Logger.Info(
                        "EnhanceChineseSearch - Tokenizer exists but SHA-1 is not recognized. No action taken.");

                    return true;
                }

                Plugin.Instance.Logger.Info("EnhanceChineseSearch - Tokenizer does not exist. Exporting...");
                ExportTokenizer(resourceName);

                return true;
            }
            catch (Exception e)
            {
                // 记录错误信息，但只在 Debug 模式下记录详细堆栈
                Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - EnsureTokenizerExists Failed: {e.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"Exception type: {e.GetType().Name}");
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                    if (e.InnerException != null)
                    {
                        Plugin.Instance.Logger.Debug($"Inner exception: {e.InnerException.GetType().Name}: {e.InnerException.Message}");
                    }
                }
            }

            return false;
        }

        private static void ExportTokenizer(string resourceName)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                using (var fileStream = new FileStream(_tokenizerPath, FileMode.Create, FileAccess.Write))
                {
                    stream.CopyTo(fileStream);
                }
            }

            Plugin.Instance.Logger.Info($"EnhanceChineseSearch - Exported {resourceName} to {_tokenizerPath}");
        }

        private static string GetTokenizerResourceName()
        {
            var tokenizerNamespace = Assembly.GetExecutingAssembly().GetName().Name + ".Tokenizer";
            var winSimpleTokenizer = $"{tokenizerNamespace}.win.libsimple.so";
            var linuxSimpleTokenizer = $"{tokenizerNamespace}.linux.libsimple.so";

            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT when Environment.Is64BitOperatingSystem:
                    return winSimpleTokenizer;
                case PlatformID.Unix when Environment.Is64BitOperatingSystem:
                    return linuxSimpleTokenizer;
                default:
                    return null;
            }
        }

        private static Dictionary<Version, string> GetExpectedSha1()
        {
            switch (Environment.OSVersion.Platform)
            {
                case PlatformID.Win32NT:
                    return new Dictionary<Version, string>
                    {
                        { new Version(0, 4, 0), "a83d90af9fb88e75a1ddf2436c8b67954c761c83" },
                        { new Version(0, 5, 0), "aed57350b46b51bb7d04321b7fe8e5e60b0cdbdc" }
                    };
                case PlatformID.Unix:
                    return new Dictionary<Version, string>
                    {
                        { new Version(0, 4, 0), "f7fb8ba0b98e358dfaa87570dc3426ee7f00e1b6" },
                        { new Version(0, 5, 0), "8e36162f96c67d77c44b36093f31ae4d297b15c0" }
                    };
                default:
                    return null;
            }
        }

        private static string ComputeSha1(string filePath)
        {
            using (var sha1 = SHA1.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = sha1.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static void ResetOptions()
        {
            Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.EnhanceChineseSearch = false;
            Plugin.Instance.MainOptionsStore.SavePluginOptionsSuppress();
        }

        private static bool PatchSearchFunctions()
        {
            // 根据返回类型选择正确的 Postfix 方法
            string getJoinCommandTextPostfix = _getJoinCommandTextReturnsStringBuilder 
                ? nameof(GetJoinCommandTextPostfixStringBuilder) 
                : nameof(GetJoinCommandTextPostfix);
            
            return PatchUnpatch(Instance.PatchTracker, true, _getJoinCommandText,
                       postfix: getJoinCommandTextPostfix) &&
                   PatchUnpatch(Instance.PatchTracker, true, _createSearchTerm,
                       prefix: nameof(CreateSearchTermPrefix)) &&
                   PatchUnpatch(Instance.PatchTracker, true,
                       _cacheIdsFromTextParams, prefix: nameof(CacheIdsFromTextParamsPrefix));
        }

        private static bool LoadTokenizerExtension(IDatabaseConnection connection)
        {
            try
            {
                // 检查必需的组件是否存在
                if (sqlite3_db == null)
                {
                    if (Plugin.Instance.DebugMode)
                    {
                        Plugin.Instance.Logger.Debug("EnhanceChineseSearch: sqlite3_db field not found, cannot load tokenizer");
                    }
                    return false;
                }
                
                if (sqlite3_enable_load_extension == null || raw == null)
                {
                    if (Plugin.Instance.DebugMode)
                    {
                        Plugin.Instance.Logger.Debug("EnhanceChineseSearch: sqlite3_enable_load_extension method or raw type not found, cannot load tokenizer");
                    }
                    return false;
                }
                
                if (string.IsNullOrEmpty(_tokenizerPath) || !File.Exists(_tokenizerPath))
                {
                    if (Plugin.Instance.DebugMode)
                    {
                        Plugin.Instance.Logger.Debug($"EnhanceChineseSearch: Tokenizer file not found at {_tokenizerPath}");
                    }
                    return false;
                }

                var db = sqlite3_db.GetValue(connection);
                if (db == null)
                {
                    if (Plugin.Instance.DebugMode)
                    {
                        Plugin.Instance.Logger.Debug("EnhanceChineseSearch: Could not get SQLite database handle from connection");
                    }
                    return false;
                }
                
                sqlite3_enable_load_extension.Invoke(raw, new[] { db, 1 });
                connection.Execute("SELECT load_extension('" + _tokenizerPath + "')");

                return true;
            }
            catch (Exception e)
            {
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Warn("EnhanceChineseSearch - Load tokenizer failed.");
                    Plugin.Instance.Logger.Debug(e.Message);
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }
                else
                {
                    Plugin.Instance.Logger.Warn($"EnhanceChineseSearch - Load tokenizer failed: {e.Message}");
                }
            }

            return false;
        }

        [HarmonyPostfix]
        private static void CreateConnectionPostfix(object __instance, ref IDatabaseConnection __result)
        {
            // 不再依赖 isReadOnly 参数,因为某些 Emby 版本的 CreateConnection 方法没有此参数
            // 直接检查是否已初始化和数据库文件路径
            if (!_patchPhase2Initialized)
            {
                lock (_lock)
                {
                    if (!_patchPhase2Initialized)
                    {
                        try
                        {
                            // 尝试获取数据库文件路径，如果属性不存在则尝试其他方式
                            string db = null;
                            
                            if (_dbFilePath != null)
                            {
                                db = _dbFilePath.GetValue(__instance) as string;
                            }
                            else
                            {
                                // 如果 DbFilePath 属性不存在，尝试通过反射查找其他可能的属性或字段
                                var instanceType = __instance.GetType();
                                
                                // 尝试查找 DbFilePath 属性（可能名称不同）
                                var dbFilePathProp = instanceType.GetProperty("DbFilePath", 
                                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                if (dbFilePathProp != null)
                                {
                                    db = dbFilePathProp.GetValue(__instance) as string;
                                }
                                else
                                {
                                    // 尝试查找 _dbFilePath 字段
                                    var dbFilePathField = instanceType.GetField("_dbFilePath", 
                                        BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (dbFilePathField != null)
                                    {
                                        db = dbFilePathField.GetValue(__instance) as string;
                                    }
                                    else
                                    {
                                        // 尝试查找 dbFilePath 字段（小写开头）
                                        dbFilePathField = instanceType.GetField("dbFilePath", 
                                            BindingFlags.NonPublic | BindingFlags.Instance);
                                        if (dbFilePathField != null)
                                        {
                                            db = dbFilePathField.GetValue(__instance) as string;
                                        }
                                    }
                                }
                            }
                            
                            // 如果无法获取路径，或者路径不是 library.db，则跳过
                            // 在新版本中，如果无法确定路径，我们仍然尝试加载 tokenizer
                            // 因为可能只有 library.db 会调用 CreateConnection
                            if (db != null && !db.EndsWith("library.db", StringComparison.OrdinalIgnoreCase))
                            {
                                if (Plugin.Instance.DebugMode)
                                {
                                    Plugin.Instance.Logger.Debug($"EnhanceChineseSearch: Skipping non-library database: {db}");
                                }
                                return;
                            }
                            
                            // 如果 db 为 null，可能是新版本中路径获取方式改变了
                            // 在这种情况下，我们仍然尝试加载，但记录警告
                            if (db == null && Plugin.Instance.DebugMode)
                            {
                                Plugin.Instance.Logger.Debug("EnhanceChineseSearch: Could not determine database path, attempting to load tokenizer anyway");
                            }

                            var tokenizerLoaded = LoadTokenizerExtension(__result);

                            if (tokenizerLoaded)
                            {
                                _patchPhase2Initialized = true;
                                PatchPhase2(__result);
                            }
                        }
                        catch (Exception ex)
                        {
                            // 捕获所有异常，避免影响正常的数据库连接创建
                            // 只在 Debug 模式下记录详细错误，避免日志噪音
                            if (Plugin.Instance.DebugMode)
                            {
                                Plugin.Instance.Logger.Debug($"EnhanceChineseSearch: CreateConnectionPostfix error: {ex.Message}");
                                Plugin.Instance.Logger.Debug($"Exception type: {ex.GetType().Name}");
                                Plugin.Instance.Logger.Debug(ex.StackTrace);
                                if (ex.InnerException != null)
                                {
                                    Plugin.Instance.Logger.Debug($"Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                                }
                            }
                            // 对于非关键错误，不记录警告，避免日志噪音
                            // 只有在真正影响功能时才记录警告
                        }
                    }
                }
            }
        }

        [HarmonyPostfix]
        private static void GetJoinCommandTextPostfix(InternalItemsQuery query,
            List<KeyValuePair<string, string>> bindParams, string mediaItemsTableQualifier, 
            bool allowJoinOnItemLinks, ref string __result)
        {
            if (!string.IsNullOrEmpty(query.SearchTerm) && __result.Contains("match @SearchTerm"))
            {
                if (!Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.ExcludeOriginalTitleFromSearch)
                {
                    __result = __result.Replace("match @SearchTerm", "match simple_query(@SearchTerm)");
                }
                else
                {
                    __result = __result.Replace("match @SearchTerm", "match '-OriginalTitle:' || simple_query(@SearchTerm)");
                }
            }

            if (!string.IsNullOrEmpty(query.Name) && __result.Contains("match @SearchTerm"))
            {
                __result = __result.Replace("match @SearchTerm", "match 'Name:' || simple_query(@SearchTerm)");

                for (var i = 0; i < bindParams.Count; i++)
                {
                    var kvp = bindParams[i];
                    if (kvp.Key == "@SearchTerm")
                    {
                        var currentValue = kvp.Value;

                        if (currentValue.StartsWith("Name:", StringComparison.Ordinal))
                        {
                            currentValue = currentValue
                                .Substring(currentValue.IndexOf(":", StringComparison.Ordinal) + 1)
                                .Trim('\"', '^', '$')
                                .Replace(".", string.Empty)
                                .Replace("'", string.Empty);
                        }

                        bindParams[i] = new KeyValuePair<string, string>(kvp.Key, currentValue);
                    }
                }
            }
        }

        [HarmonyPostfix]
        private static void GetJoinCommandTextPostfixStringBuilder(InternalItemsQuery query,
            List<KeyValuePair<string, string>> bindParams, string mediaItemsTableQualifier,
            bool allowJoinOnItemLinks, System.Text.StringBuilder __result)
        {
            if (__result == null) return;
            
            var resultString = __result.ToString();
            var modified = false;

            if (!string.IsNullOrEmpty(query.SearchTerm) && resultString.Contains("match @SearchTerm"))
            {
                if (!Plugin.Instance.MainOptionsStore.GetOptions().ModOptions.ExcludeOriginalTitleFromSearch)
                {
                    resultString = resultString.Replace("match @SearchTerm", "match simple_query(@SearchTerm)");
                    modified = true;
                }
                else
                {
                    resultString = resultString.Replace("match @SearchTerm", "match '-OriginalTitle:' || simple_query(@SearchTerm)");
                    modified = true;
                }
            }

            if (!string.IsNullOrEmpty(query.Name) && resultString.Contains("match @SearchTerm"))
            {
                resultString = resultString.Replace("match @SearchTerm", "match 'Name:' || simple_query(@SearchTerm)");
                modified = true;

                for (var i = 0; i < bindParams.Count; i++)
                {
                    var kvp = bindParams[i];
                    if (kvp.Key == "@SearchTerm")
                    {
                        var currentValue = kvp.Value;

                        if (currentValue.StartsWith("Name:", StringComparison.Ordinal))
                        {
                            currentValue = currentValue
                                .Substring(currentValue.IndexOf(":", StringComparison.Ordinal) + 1)
                                .Trim('\"', '^', '$')
                                .Replace(".", string.Empty)
                                .Replace("'", string.Empty);
                        }

                        bindParams[i] = new KeyValuePair<string, string>(kvp.Key, currentValue);
                    }
                }
            }

            if (modified)
            {
                __result.Clear();
                __result.Append(resultString);
            }
        }

        [HarmonyPrefix]
        private static bool CreateSearchTermPrefix(string searchTerm, ref string __result)
        {
            __result = searchTerm.Replace(".", string.Empty).Replace("'", string.Empty);

            return false;
        }

        [HarmonyPrefix]
        private static bool CacheIdsFromTextParamsPrefix(InternalItemsQuery query, IDatabaseConnection db)
        {
            if ((query.PersonTypes?.Length ?? 0) == 0)
            {
                var nameStartsWith = query.NameStartsWith;
                if (!string.IsNullOrEmpty(nameStartsWith))
                {
                    query.SearchTerm = nameStartsWith;
                    query.NameStartsWith = null;
                }

                var searchTerm = query.SearchTerm;
                if (query.IncludeItemTypes.Length == 0 && !string.IsNullOrEmpty(searchTerm))
                {
                    query.IncludeItemTypes = GetSearchScope();
                }

                if (!string.IsNullOrEmpty(searchTerm))
                {
                    foreach (var provider in patterns)
                    {
                        var match = provider.Value.Match(searchTerm.Trim());
                        if (match.Success)
                        {
                            var idValue = provider.Key == "imdb" ? match.Value : match.Groups[2].Value;

                            query.AnyProviderIdEquals = new List<KeyValuePair<string, string>>
                            {
                                new KeyValuePair<string, string>(provider.Key, idValue)
                            };
                            query.SearchTerm = null;
                            break;
                        }
                    }
                }

                if (AppVer >= Ver4937 && !string.IsNullOrEmpty(query.SearchTerm))
                {
                    var result = LoadTokenizerExtension(db);
                }
            }

            return true;
        }
    }
}
