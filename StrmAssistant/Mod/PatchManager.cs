using HarmonyLib;
using StrmAssistant.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace StrmAssistant.Mod
{
    public static class PatchManager
    {
        public static Harmony HarmonyMod;
        public static readonly List<PatchTracker> PatchTrackerList = new List<PatchTracker>();
        
        private static bool _isInitialized = false;

        public static EnableImageCapture EnableImageCapture;
        public static EnhanceChineseSearch EnhanceChineseSearch;
        public static MergeMultiVersion MergeMultiVersion;
        public static ExclusiveExtract ExclusiveExtract;
        public static ChineseMovieDb ChineseMovieDb;
        public static ChineseTvdb ChineseTvdb;
        public static EnhanceMovieDbPerson EnhanceMovieDbPerson;
        public static AltMovieDbConfig AltMovieDbConfig;
        public static EnableProxyServer EnableProxyServer;
        public static PreferOriginalPoster PreferOriginalPoster;
        public static UnlockIntroSkip UnlockIntroSkip;
        public static PinyinSortName PinyinSortName;
        public static EnhanceNfoMetadata EnhanceNfoMetadata;
        public static HidePersonNoImage HidePersonNoImage;
        public static EnforceLibraryOrder EnforceLibraryOrder;
        public static BeautifyMissingMetadata BeautifyMissingMetadata;
        public static EnhanceMissingEpisodes EnhanceMissingEpisodes;
        public static ChapterChangeTracker ChapterChangeTracker;
        public static MovieDbEpisodeGroup MovieDbEpisodeGroup;
        public static NoBoxsetsAutoCreation NoBoxsetsAutoCreation;
        public static EnhanceNotificationSystem EnhanceNotificationSystem;
        public static EnableDeepDelete EnableDeepDelete;
        public static SuppressPluginUpdate SuppressPluginUpdate;

        private static readonly ConcurrentDictionary<Tuple<Type, string>, HarmonyMethod> HarmonyMethodCache 
            = new ConcurrentDictionary<Tuple<Type, string>, HarmonyMethod>();
        private static readonly ConcurrentDictionary<Tuple<Type, string>, MethodInfo> MethodInfoCache 
            = new ConcurrentDictionary<Tuple<Type, string>, MethodInfo>();

        public static void Initialize()
        {
            if (_isInitialized)
            {
                Plugin.Instance.Logger.Warn("PatchManager already initialized");
                return;
            }
            
            try
            {
                HarmonyMod = new Harmony("emby.mod");
                Plugin.Instance.Logger.Info($"Harmony Mod initialized successfully (Emby {Plugin.Instance.ApplicationHost.ApplicationVersion})");
                _isInitialized = true;
            }
            catch (Exception e)
            {
                Plugin.Instance.Logger.Error("Harmony Init Failed - Harmony mod will not be available");
                Plugin.Instance.Logger.Error($"Error: {e.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(e.StackTrace);
                }
            }

            EnableImageCapture = new EnableImageCapture();
            EnhanceChineseSearch = new EnhanceChineseSearch();
            MovieDbEpisodeGroup = new MovieDbEpisodeGroup();
            MergeMultiVersion = new MergeMultiVersion();
            ExclusiveExtract = new ExclusiveExtract();
            ChineseMovieDb = new ChineseMovieDb();
            ChineseTvdb = new ChineseTvdb();
            EnhanceMovieDbPerson = new EnhanceMovieDbPerson();
            AltMovieDbConfig = new AltMovieDbConfig();
            EnableProxyServer = new EnableProxyServer();
            PreferOriginalPoster = new PreferOriginalPoster();
            UnlockIntroSkip = new UnlockIntroSkip();
            PinyinSortName = new PinyinSortName();
            EnhanceNfoMetadata = new EnhanceNfoMetadata();
            HidePersonNoImage = new HidePersonNoImage();
            EnforceLibraryOrder = new EnforceLibraryOrder();
            BeautifyMissingMetadata = new BeautifyMissingMetadata();
            EnhanceMissingEpisodes = new EnhanceMissingEpisodes();
            ChapterChangeTracker = new ChapterChangeTracker();
            NoBoxsetsAutoCreation = new NoBoxsetsAutoCreation();
            EnhanceNotificationSystem = new EnhanceNotificationSystem();
            EnableDeepDelete = new EnableDeepDelete();
            SuppressPluginUpdate = new SuppressPluginUpdate();
        }

        public static bool IsPatched(MethodBase methodInfo, Type type)
        {
            var patchedMethods = Harmony.GetAllPatchedMethods();
            if (!patchedMethods.Contains(methodInfo)) return false;
            var patchInfo = Harmony.GetPatchInfo(methodInfo);

            return patchInfo.Prefixes.Any(p => p.owner == HarmonyMod.Id && p.PatchMethod.DeclaringType == type) ||
                   patchInfo.Postfixes.Any(p => p.owner == HarmonyMod.Id && p.PatchMethod.DeclaringType == type) ||
                   patchInfo.Transpilers.Any(p => p.owner == HarmonyMod.Id && p.PatchMethod.DeclaringType == type) ||
                   patchInfo.Finalizers.Any(p => p.owner == HarmonyMod.Id && p.PatchMethod.DeclaringType == type);
        }

        public static bool WasCalledByMethod(Assembly assembly, string callingMethodName)
        {
            var stackFrames = new StackTrace(1, false).GetFrames();

            return stackFrames.Any(f =>
            {
                var method = f?.GetMethod();
                return method?.DeclaringType?.Assembly == assembly && method?.Name == callingMethodName;
            });
        }

        private static bool? _lastModSuccessStatus = null;
        private static string _lastStatusLog = null;
        
        public static bool IsModSuccess()
        {
            var supportedPatches = PatchTrackerList.Where(p => p.IsSupported).ToList();
            
            // 定义可选功能补丁（这些功能不可用时不应该影响整体状态）
            var optionalFeatureTypes = new[]
            {
                typeof(EnhanceChineseSearch),
                typeof(PreferOriginalPoster),
                typeof(SuppressPluginUpdate),
                typeof(ChineseTvdb),
                typeof(NoBoxsetsAutoCreation),
                typeof(EnableImageCapture),
                typeof(AltMovieDbConfig),
                typeof(ChineseMovieDb),
                typeof(EnhanceMovieDbPerson),
                typeof(EnhanceNfoMetadata),
                typeof(MovieDbEpisodeGroup)
            };
            
            // 核心功能补丁（这些失败会影响整体状态）
            var corePatches = supportedPatches.Where(p => !optionalFeatureTypes.Contains(p.PatchType)).ToList();
            // 可选功能补丁
            var optionalPatches = supportedPatches.Where(p => optionalFeatureTypes.Contains(p.PatchType)).ToList();
            
            // 只有真正失败的补丁（FallbackPatchApproach为None）才认为是失败
            // Reflection回退是完全正常和可接受的，功能仍然可用
            var failedCorePatches = corePatches.Where(p => p.FallbackPatchApproach == PatchApproach.None).ToList();
            var failedOptionalPatches = optionalPatches.Where(p => p.FallbackPatchApproach == PatchApproach.None).ToList();
            
            // 使用Reflection的补丁（即使默认是Harmony）也应该被视为成功
            var reflectionPatches = supportedPatches.Where(p => 
                p.DefaultPatchApproach == PatchApproach.Harmony && 
                p.FallbackPatchApproach == PatchApproach.Reflection).ToList();
            
            // 构建状态字符串用于比较，避免重复日志
            var statusLog = failedCorePatches.Any() 
                ? $"CoreFailed:{failedCorePatches.Count}/{corePatches.Count}" + string.Join(",", failedCorePatches.Select(p => $"{p.PatchType.Name}"))
                : failedOptionalPatches.Any()
                    ? $"OptionalFailed:{failedOptionalPatches.Count}/{optionalPatches.Count}"
                    : reflectionPatches.Any()
                        ? $"Reflection:{reflectionPatches.Count}/{supportedPatches.Count}"
                        : "AllSuccess";
            
            // 只在状态改变或首次调用时记录日志
            if (_lastStatusLog != statusLog)
            {
                _lastStatusLog = statusLog;
                
                if (failedCorePatches.Any())
                {
                    Plugin.Instance.Logger.Error($"=== Harmony Mod Status: {failedCorePatches.Count} core patches unavailable ===");
                    
                    foreach (var patch in failedCorePatches)
                    {
                        Plugin.Instance.Logger.Error($"  ✗ {patch.PatchType.Name} - Core feature disabled (required method/plugin not found)");
                    }
                    
                    Plugin.Instance.Logger.Error($"Some core features are not available. Plugin may not function correctly.");
                }
                else if (failedOptionalPatches.Any())
                {
                    Plugin.Instance.Logger.Info($"=== Harmony Mod Status: Core features working, {failedOptionalPatches.Count} optional features unavailable ===");
                    
                    if (Plugin.Instance.DebugMode)
                    {
                        foreach (var patch in failedOptionalPatches)
                        {
                            Plugin.Instance.Logger.Debug($"  - {patch.PatchType.Name} - Optional feature disabled (plugin/dependency not installed)");
                        }
                    }
                    
                    Plugin.Instance.Logger.Info("All core features are available. Some optional features may require additional plugins.");
                }
                else if (reflectionPatches.Any())
                {
                    Plugin.Instance.Logger.Info($"=== Harmony Mod Status: All patches working (using Reflection for {reflectionPatches.Count} patches) ===");
                    if (Plugin.Instance.DebugMode)
                    {
                        foreach (var patch in reflectionPatches)
                        {
                            Plugin.Instance.Logger.Debug($"  ✓ {patch.PatchType.Name} - Using Reflection (Harmony ReversePatch not available, but Reflection works fine)");
                        }
                    }
                    Plugin.Instance.Logger.Info("All features are available and working correctly.");
                }
                else if (supportedPatches.Any())
                {
                    Plugin.Instance.Logger.Info($"Harmony Mod: All {supportedPatches.Count} patches initialized successfully");
                }
            }
            
            // 只有核心功能失败时才返回false
            // 可选功能失败和Reflection回退都不应该被视为失败
            var result = failedCorePatches.Count == 0;
            _lastModSuccessStatus = result;
            return result;
        }

        public static void CopyProperty(object source, object target, string propertyName)
        {
            var value = Traverse.Create(source).Property(propertyName).GetValue();
            Traverse.Create(target).Property(propertyName).SetValue(value);
        }

        public static bool ReversePatch(PatchTracker tracker, MethodBase targetMethod, string stub, bool suppressWarnings = false)
        {
            if (tracker.FallbackPatchApproach != PatchApproach.Harmony) return false;

            if (targetMethod is null)
            {
                if (!suppressWarnings)
                {
                    // 对于可选功能，使用 Debug 级别而不是 Warn
                    if (tracker.PatchType == typeof(PreferOriginalPoster) || 
                        tracker.PatchType == typeof(ChineseTvdb) ||
                        tracker.PatchType == typeof(ChineseMovieDb))
                    {
                        if (Plugin.Instance.DebugMode)
                        {
                            Plugin.Instance.Logger.Debug($"{tracker.PatchType.Name} ReversePatch: Target method is null (optional feature, may require additional plugins)");
                        }
                    }
                    else
                    {
                        Plugin.Instance.Logger.Warn($"{tracker.PatchType.Name} ReversePatch Failed: Target method is null");
                    }
                }
                // 对于可选功能，不应该将 FallbackPatchApproach 设置为 None
                // 因为即使 ReversePatch 失败，功能可能仍然可用（通过 Reflection）
                if (tracker.PatchType != typeof(PreferOriginalPoster) && 
                    tracker.PatchType != typeof(ChineseTvdb) &&
                    tracker.PatchType != typeof(ChineseMovieDb))
                {
                    tracker.FallbackPatchApproach = PatchApproach.None;
                }
                return false;
            }

            var stubMethod = GetHarmonyMethod(tracker.PatchType, stub);

            if (stubMethod == null)
            {
                if (!suppressWarnings)
                {
                    Plugin.Instance.Logger.Warn($"{tracker.PatchType.Name} ReversePatch Failed: Stub method '{stub}' not found");
                }
                tracker.FallbackPatchApproach = PatchApproach.Reflection;
                return false;
            }

            try
            {
                var methodName = targetMethod.DeclaringType != null ? $"{targetMethod.DeclaringType.Name}.{targetMethod.Name}" : targetMethod.Name;
                
                // 尝试创建ReversePatcher并应用补丁
                var reversePatcher = HarmonyMod.CreateReversePatcher(targetMethod, stubMethod);
                reversePatcher.Patch();

                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug(
                        $"{nameof(ReversePatch)} {methodName} for {tracker.PatchType.Name} Success");
                }

                return true;
            }
            catch (Exception he)
            {
                var methodName = targetMethod.DeclaringType != null ? $"{targetMethod.DeclaringType.Name}.{targetMethod.Name}" : targetMethod.Name;
                var exceptionType = he.GetType().Name;
                var exceptionMessage = he.Message;
                
                // 针对特定错误类型提供更详细的说明
                string detailedMessage = exceptionMessage;
                bool isKnownLimitation = false;
                
                if (exceptionMessage.Contains("Common Language Runtime detected an invalid program") ||
                    (exceptionMessage.Contains("CLR") && exceptionMessage.Contains("invalid program")))
                {
                    detailedMessage = "Harmony无法反编译此方法（可能包含复杂IL代码或泛型约束）。这是Harmony的已知限制，不影响功能，将使用反射方式。";
                    isKnownLimitation = true;
                    if (!Plugin.Instance.DebugMode)
                    {
                        Plugin.Instance.Logger.Info($"{tracker.PatchType.Name}: {detailedMessage}");
                    }
                    else
                    {
                        Plugin.Instance.Logger.Warn($"{tracker.PatchType.Name} ReversePatch: {detailedMessage}");
                        Plugin.Instance.Logger.Warn($"  Method: {methodName}");
                    }
                }
                else if (exceptionMessage.Contains("target of an invocation"))
                {
                    var innerException = he.InnerException;
                    detailedMessage = $"反射调用失败: {exceptionMessage}";
                    if (innerException != null)
                    {
                        detailedMessage += $" (Inner: {innerException.GetType().Name}: {innerException.Message})";
                    }
                    Plugin.Instance.Logger.Warn($"{tracker.PatchType.Name} ReversePatch Failed: {detailedMessage}");
                    Plugin.Instance.Logger.Warn($"  Method: {methodName}");
                    Plugin.Instance.Logger.Warn($"  This usually means the method signature or implementation has changed in this Emby version.");
                }
                else if (exceptionMessage.Contains("Method not found") || exceptionMessage.Contains("does not exist"))
                {
                    Plugin.Instance.Logger.Warn($"{tracker.PatchType.Name} ReversePatch Failed: Method not found or signature changed");
                    Plugin.Instance.Logger.Warn($"  Method: {methodName}");
                    Plugin.Instance.Logger.Warn($"  This Emby version may have changed the internal API. Attempting fallback...");
                }
                else
                {
                    Plugin.Instance.Logger.Warn($"{tracker.PatchType.Name} ReversePatch Failed: {exceptionMessage}");
                    Plugin.Instance.Logger.Warn($"  Method: {methodName}");
                }
                
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"Stub method: {stubMethod.method.Name}");
                    Plugin.Instance.Logger.Debug($"Exception type: {exceptionType}");
                    Plugin.Instance.Logger.Debug($"Full stack trace:");
                    Plugin.Instance.Logger.Debug(he.StackTrace);
                    if (he.InnerException != null)
                    {
                        Plugin.Instance.Logger.Debug($"Inner exception: {he.InnerException.GetType().Name}: {he.InnerException.Message}");
                        Plugin.Instance.Logger.Debug(he.InnerException.StackTrace);
                    }
                }

                tracker.FallbackPatchApproach = PatchApproach.Reflection;
                
                if (!isKnownLimitation)
                {
                    Plugin.Instance.Logger.Info($"{tracker.PatchType.Name} will use Reflection approach as fallback (this is normal and expected)");
                }
            }

            return false;
        }

        public static bool PatchUnpatch(PatchTracker tracker, bool apply, MethodBase targetMethod, string prefix = null,
            string postfix = null, string transpiler = null, string finalizer = null, bool suppress = false)
        {
            if (tracker.FallbackPatchApproach != PatchApproach.Harmony) return false;

            if (targetMethod is null)
            {
                Plugin.Instance.Logger.Warn($"{tracker.PatchType.Name} PatchUnpatch Failed: Target method is null");
                tracker.FallbackPatchApproach = PatchApproach.None;
                return false;
            }

            if (HarmonyMod == null)
            {
                Plugin.Instance.Logger.Error($"{tracker.PatchType.Name} PatchUnpatch Failed: HarmonyMod is not initialized");
                tracker.FallbackPatchApproach = PatchApproach.Reflection;
                return false;
            }

            var action = apply ? "Patch" : "Unpatch";

            try
            {
                if (apply && !IsPatched(targetMethod, tracker.PatchType))
                {
                    var prefixMethod = GetHarmonyMethod(tracker.PatchType, prefix);
                    var postfixMethod = GetHarmonyMethod(tracker.PatchType, postfix);
                    var transpilerMethod = GetHarmonyMethod(tracker.PatchType, transpiler);
                    var finalizerMethod = GetHarmonyMethod(tracker.PatchType, finalizer);

                    HarmonyMod.Patch(targetMethod, prefixMethod, postfixMethod, transpilerMethod, finalizerMethod);
                }
                else if (!apply && IsPatched(targetMethod, tracker.PatchType))
                {
                    var prefixMethod = GetMethodInfo(tracker.PatchType, prefix);
                    var postfixMethod = GetMethodInfo(tracker.PatchType, postfix);
                    var transpilerMethod = GetMethodInfo(tracker.PatchType, transpiler);
                    var finalizerMethod = GetMethodInfo(tracker.PatchType, finalizer);

                    if (prefixMethod != null) HarmonyMod.Unpatch(targetMethod, prefixMethod);
                    if (postfixMethod != null) HarmonyMod.Unpatch(targetMethod, postfixMethod);
                    if (transpilerMethod != null) HarmonyMod.Unpatch(targetMethod, transpilerMethod);
                    if (finalizerMethod != null) HarmonyMod.Unpatch(targetMethod, finalizerMethod);
                }

                if (!suppress)
                {
                    if (Plugin.Instance.DebugMode)
                    {
                        Plugin.Instance.Logger.Debug(
                            $"{action} {(targetMethod.DeclaringType != null ? targetMethod.DeclaringType.Name + "." : string.Empty)}{targetMethod.Name} for {tracker.PatchType.Name} Success");
                    }
                }

                return true;
            }
            catch (Exception he)
            {
                var methodName = targetMethod.DeclaringType != null ? $"{targetMethod.DeclaringType.Name}.{targetMethod.Name}" : targetMethod.Name;
                
                // 检测常见错误类型并提供更好的诊断信息
                bool canFallbackToReflection = true;
                
                // 检测参数不匹配的情况 - 这通常是由于 Emby 版本差异导致的
                if (he.Message.Contains("Parameter") && he.Message.Contains("not found"))
                {
                    // 对于参数不匹配,我们应该尝试用更宽松的方式 patch
                    // 而不是直接失败或回退到反射
                    Plugin.Instance.Logger.Warn($"{tracker.PatchType.Name} {action} Failed: {he.Message}");
                    Plugin.Instance.Logger.Warn($"  Target: {methodName}");
                    
                    // 参数不匹配通常意味着目标方法签名与 patch 方法签名不一致
                    // 这种情况下反射也帮不上忙,应该标记为 None
                    canFallbackToReflection = false;
                }
                else if (he is ArgumentException && he.Message.Contains("Method") && he.Message.Contains("not found"))
                {
                    Plugin.Instance.Logger.Warn($"{tracker.PatchType.Name} {action} Failed: Target method not found");
                    Plugin.Instance.Logger.Warn($"  Method: {methodName}");
                    Plugin.Instance.Logger.Warn($"  This Emby version may have removed or renamed this method.");
                    canFallbackToReflection = false;
                }
                else if (he.Message.Contains("IL") || he.Message.Contains("instruction"))
                {
                    Plugin.Instance.Logger.Warn($"{tracker.PatchType.Name} {action} Failed: IL manipulation error");
                    Plugin.Instance.Logger.Warn($"  Method: {methodName}");
                    Plugin.Instance.Logger.Info($"  The method IL structure may have changed. Attempting reflection fallback...");
                }
                else
                {
                    Plugin.Instance.Logger.Warn($"{tracker.PatchType.Name} {action} Failed: {he.Message}");
                    Plugin.Instance.Logger.Warn($"  Target: {methodName}");
                }
                
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"Exception type: {he.GetType().Name}");
                    Plugin.Instance.Logger.Debug($"Prefix: {prefix}, Postfix: {postfix}, Transpiler: {transpiler}, Finalizer: {finalizer}");
                    Plugin.Instance.Logger.Debug(he.StackTrace);
                    if (he.InnerException != null)
                    {
                        Plugin.Instance.Logger.Debug($"Inner exception: {he.InnerException}");
                    }
                }

                tracker.FallbackPatchApproach = canFallbackToReflection ? PatchApproach.Reflection : PatchApproach.None;
                
                if (canFallbackToReflection)
                {
                    Plugin.Instance.Logger.Info($"{tracker.PatchType.Name} will use Reflection approach as fallback");
                }
                else
                {
                    Plugin.Instance.Logger.Error($"{tracker.PatchType.Name} feature is not available on this Emby version");
                }
            }

            return false;
        }

        public static bool PatchUnpatch(PatchTracker tracker, bool apply, MethodBase targetMethod, ref int usageCount,
            string prefix = null, string postfix = null, string transpiler = null, string finalizer = null,
            bool suppress = false)
        {
            if (apply)
            {
                if (usageCount == 0)
                {
                    if (PatchUnpatch(tracker, true, targetMethod, prefix, postfix, transpiler, finalizer, suppress))
                    {
                        usageCount++;
                        return true;
                    }

                    return false;
                }

                usageCount++;
            }
            else
            {
                if (usageCount <= 0)
                    throw new InvalidOperationException();

                usageCount--;

                if (usageCount == 0)
                {
                    return PatchUnpatch(tracker, false, targetMethod, prefix, postfix, transpiler, finalizer, suppress);
                }
            }

            return true;
        }

        private static HarmonyMethod GetHarmonyMethod(Type patchType, string patchMethod)
        {
            if (string.IsNullOrEmpty(patchMethod)) return null;

            return HarmonyMethodCache.GetOrAdd(Tuple.Create(patchType, patchMethod), tuple =>
            {
                var methodInfo = GetMethodInfo(tuple.Item1, tuple.Item2);
                return methodInfo != null ? new HarmonyMethod(methodInfo) : null;
            });
        }

        private static MethodInfo GetMethodInfo(Type patchType, string patchMethod)
        {
            if (string.IsNullOrEmpty(patchMethod)) return null;

            return MethodInfoCache.GetOrAdd(Tuple.Create(patchType, patchMethod),
                tuple => AccessTools.Method(tuple.Item1, tuple.Item2));
        }
        
        /// <summary>
        /// 获取所有补丁的诊断报告
        /// </summary>
        public static string GetDiagnosticReport()
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine("=== Patch Manager Diagnostic Report ===");
            report.AppendLine($"Emby Version: {Plugin.Instance.ApplicationHost.ApplicationVersion}");
            report.AppendLine($"Harmony Initialized: {HarmonyMod != null}");
            report.AppendLine($"Total Patches: {PatchTrackerList.Count}");
            report.AppendLine();
            
            var runningPatches = PatchTrackerList.Where(p => p.IsRunning).ToList();
            var failedPatches = PatchTrackerList.Where(p => !p.IsRunning && p.IsSupported).ToList();
            
            report.AppendLine($"Running Patches: {runningPatches.Count}");
            foreach (var patch in runningPatches.OrderBy(p => p.PatchType.Name))
            {
                var method = patch.FallbackPatchApproach == patch.DefaultPatchApproach 
                    ? patch.FallbackPatchApproach.ToString() 
                    : $"{patch.DefaultPatchApproach} → {patch.FallbackPatchApproach}";
                report.AppendLine($"  ✓ {patch.PatchType.Name}: {method}");
            }
            
            if (failedPatches.Any())
            {
                report.AppendLine();
                report.AppendLine($"Failed Patches: {failedPatches.Count}");
                foreach (var patch in failedPatches.OrderBy(p => p.PatchType.Name))
                {
                    report.AppendLine($"  ✗ {patch.PatchType.Name}");
                    if (patch.HasErrors)
                    {
                        foreach (var error in patch.ErrorMessages.Take(3))
                        {
                            report.AppendLine($"    - {error}");
                        }
                    }
                }
            }
            
            report.AppendLine("======================================");
            return report.ToString();
        }
        
        /// <summary>
        /// 清除所有缓存
        /// </summary>
        public static void ClearCaches()
        {
            HarmonyMethodCache.Clear();
            MethodInfoCache.Clear();
            Plugin.Instance.Logger.Debug("PatchManager caches cleared");
        }
    }
}
