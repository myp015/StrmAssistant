using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using StrmAssistant.Common;
using StrmAssistant.ScheduledTask;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Common.LanguageUtility;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class EnhanceMovieDbPerson : PatchBase<EnhanceMovieDbPerson>
    {
        private static Assembly _movieDbAssembly;

        // 核心方法引用
        private static MethodInfo _movieDbPersonProviderImportData;
        private static MethodInfo _movieDbSeasonProviderImportData;
        private static MethodInfo _movieDbSeriesProviderImportData; // 新增：用于修复 ChineseMovieDb 报错
        private static MethodInfo _seasonGetMetadata;
        private static MethodInfo _addPerson;
        private static MethodInfo _ensurePersonInfoAsync;
        private static FieldInfo _cacheTime;

        private static readonly ConcurrentDictionary<Season, List<PersonInfo>> SeasonPersonInfoDictionary =
            new ConcurrentDictionary<Season, List<PersonInfo>>();

        public EnhanceMovieDbPerson()
        {
            Initialize();

            if (Plugin.Instance.MetadataEnhanceStore.GetOptions().EnhanceMovieDbPerson)
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            _movieDbAssembly = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "MovieDb");

            if (_movieDbAssembly != null)
            {
                // 1. 修复 PersonProvider (匹配 3 参数版: Person, PersonResult, bool)
                var movieDbPersonProvider = _movieDbAssembly.GetType("MovieDb.MovieDbPersonProvider");
                _movieDbPersonProviderImportData = movieDbPersonProvider?
                    .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "ImportData" && m.GetParameters().Length == 3);

                var ensurePersonInfo = movieDbPersonProvider?.GetMethod("EnsurePersonInfo", BindingFlags.NonPublic | BindingFlags.Instance);
                if (ensurePersonInfo != null) _ensurePersonInfoAsync = AccessTools.AsyncMoveNext(ensurePersonInfo);

                // 2. 修复 SeasonProvider (匹配 5 参数版)
                var movieDbSeasonProvider = _movieDbAssembly.GetType("MovieDb.MovieDbSeasonProvider");
                _movieDbSeasonProviderImportData = movieDbSeasonProvider?
                    .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "ImportData" && m.GetParameters().Length == 5);

                // 3. 【重点修复】修复 SeriesProvider (解决日志中的 Ambiguous match found 报错)
                // 对应签名: (MetadataResult`1, SeriesRootObject, String, TmdbSettingsResult, Boolean)
                var movieDbSeriesProvider = _movieDbAssembly.GetType("MovieDb.MovieDbSeriesProvider");
                _movieDbSeriesProviderImportData = movieDbSeriesProvider?
                    .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "ImportData" && m.GetParameters().Length == 5);

                // 4. 其他基础反射
                var movieDbProviderBase = _movieDbAssembly.GetType("MovieDb.MovieDbProviderBase");
                _cacheTime = movieDbProviderBase?.GetField("CacheTime", BindingFlags.Public | BindingFlags.Static);

                _seasonGetMetadata = movieDbSeasonProvider?.GetMethod("GetMetadata",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(RemoteMetadataFetchOptions<SeasonInfo>), typeof(CancellationToken) }, null);

                _addPerson = typeof(PeopleHelper).GetMethod("AddPerson", BindingFlags.Static | BindingFlags.Public);
            }
            else
            {
                Plugin.Instance.Logger.Error("EnhanceMovieDbPerson - MovieDb Assembly Not Found");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                PatchTracker.IsSupported = false;
            }
        }

        protected override void Prepare(bool apply)
        {
            // 每一个 Patch 都增加 null 检查，增强稳定性
            if (_movieDbPersonProviderImportData != null)
                PatchUnpatch(PatchTracker, apply, _movieDbPersonProviderImportData, prefix: nameof(PersonImportDataPrefix));
            
            if (_ensurePersonInfoAsync != null)
                PatchUnpatch(PatchTracker, apply, _ensurePersonInfoAsync, transpiler: nameof(EnsurePersonInfoAsyncTranspiler));
            
            if (_movieDbSeasonProviderImportData != null)
                PatchUnpatch(PatchTracker, apply, _movieDbSeasonProviderImportData, prefix: nameof(SeasonImportDataPrefix));

            // 这里如果 ChineseMovieDb 需要 patch Series，也会共享这个逻辑，不再冲突
            if (_movieDbSeriesProviderImportData != null)
            {
                // 注意：如果你的 ChineseMovieDb 类有单独的 Patch 逻辑，请确保它也引用了这里的 _movieDbSeriesProviderImportData
                Plugin.Instance.Logger.Info("ChineseMovieDb - 成功定位到 5 参数版 ImportData，歧义已消除");
            }

            if (_seasonGetMetadata != null)
                PatchUnpatch(PatchTracker, apply, _seasonGetMetadata, postfix: nameof(SeasonGetMetadataPostfix));
            
            if (_addPerson != null)
                PatchUnpatch(PatchTracker, apply, _addPerson, prefix: nameof(AddPersonPrefix));
        }

        // --- 以下逻辑保持功能性不变 ---

        private static TimeSpan GetPersonCacheTime() => RefreshPersonTask.IsRunning ? TimeSpan.FromHours(48) : MetadataApi.DefaultCacheTime;

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> EnsurePersonInfoAsyncTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var codeMatcher = new CodeMatcher(instructions, generator);
            if (_cacheTime == null) return instructions;
            codeMatcher.MatchStartForward(CodeMatch.LoadsField(_cacheTime))
                .ThrowIfInvalid("Missing CacheTime field")
                .RemoveInstruction()
                .InsertAndAdvance(CodeInstruction.Call(typeof(EnhanceMovieDbPerson), nameof(GetPersonCacheTime)));
            return codeMatcher.Instructions();
        }

        [HarmonyPrefix]
        private static bool PersonImportDataPrefix(Person item, object info, bool isFirstLanguage)
        {
            if (!RefreshPersonTask.IsRunning) return true;
            var t = Traverse.Create(info);
            if (t.Property("adult").GetValue<bool>() && RefreshPersonTask.NoAdult) return true;

            var name = t.Property("name").GetValue<string>();
            var pBirth = t.Property("place_of_birth").GetValue<string>();

            if (!string.IsNullOrEmpty(name))
            {
                var res = ProcessPersonInfoAsExpected(name, pBirth);
                if (res.Item2 && !string.Equals(name, CleanPersonName(res.Item1)))
                    t.Property("name").SetValue(res.Item1);
            }
            return true;
        }

        private static Tuple<string, bool> ProcessPersonInfoAsExpected(string input, string placeOfBirth)
        {
            var isJp = HasMovieDbJapaneseFallback() && (placeOfBirth?.Contains("Japan") ?? false);
            if (IsChinese(input)) input = ConvertTraditionalToSimplified(input);
            return new Tuple<string, bool>(input, isJp ? IsChineseJapanese(input) : IsChinese(input));
        }

        [HarmonyPrefix]
        private static bool SeasonImportDataPrefix(Season item, object seasonInfo, string name, int seasonNumber, bool isFirstLanguage)
        {
            if (!isFirstLanguage) return true;
            var cast = Traverse.Create(seasonInfo).Property("credits").Property("cast").GetValue<IEnumerable<object>>();
            if (cast != null)
            {
                var list = new List<PersonInfo>();
                foreach (var actor in cast)
                {
                    var ta = Traverse.Create(actor);
                    var p = new PersonInfo { Name = ta.Property("name").GetValue<string>(), Role = ta.Property("character").GetValue<string>(), Type = PersonType.Actor };
                    var path = ta.Property("profile_path").GetValue<string>();
                    if (!string.IsNullOrEmpty(path)) p.ImageUrl = AltMovieDbConfig.CurrentMovieDbImageUrl + "/t/p/original" + path;
                    list.Add(p);
                }
                SeasonPersonInfoDictionary[item] = list;
            }
            return true;
        }

        [HarmonyPostfix]
        private static void SeasonGetMetadataPostfix(Task<MetadataResult<Season>> __result)
        {
            if (__result?.Status == TaskStatus.RanToCompletion && __result.Result?.Item != null)
            {
                if (SeasonPersonInfoDictionary.TryRemove(__result.Result.Item, out var list))
                    foreach (var p in list) __result.Result.AddPerson(p);
            }
        }

        [HarmonyPrefix]
        private static bool AddPersonPrefix(PersonInfo person) => !string.IsNullOrWhiteSpace(person?.Name);
    }
}