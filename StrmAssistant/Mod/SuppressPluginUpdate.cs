using HarmonyLib;
using MediaBrowser.Model.Updates;
using StrmAssistant.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class SuppressPluginUpdate : PatchBase<SuppressPluginUpdate>
    {
        private static MethodInfo _getAvailablePluginUpdates;

        public SuppressPluginUpdate()
        {
            Initialize();

            var suppressPluginUpdates = Plugin.Instance.ExperienceEnhanceStore.GetOptions().SuppressPluginUpdates;

            if (!string.IsNullOrWhiteSpace(suppressPluginUpdates))
            {
                Patch();
            }
        }

        protected override void OnInitialize()
        {
            try
            {
                var embyServerImplementationsAssembly = EmbyVersionCompatibility.TryLoadAssembly("Emby.Server.Implementations");
                if (embyServerImplementationsAssembly == null)
                {
                    Plugin.Instance.Logger.Warn($"{nameof(SuppressPluginUpdate)}: Failed to load Emby.Server.Implementations assembly");
                    PatchTracker.FallbackPatchApproach = PatchApproach.None;
                    
                    EmbyVersionCompatibility.LogCompatibilityInfo(
                        nameof(SuppressPluginUpdate),
                        false,
                        "Emby.Server.Implementations assembly not found");
                    return;
                }

                var installationManager = EmbyVersionCompatibility.TryGetType(
                    embyServerImplementationsAssembly,
                    "Emby.Server.Implementations.Updates.InstallationManager");
                
                if (installationManager == null)
                {
                    Plugin.Instance.Logger.Warn($"{nameof(SuppressPluginUpdate)}: InstallationManager type not found");
                    PatchTracker.FallbackPatchApproach = PatchApproach.None;
                    
                    EmbyVersionCompatibility.LogCompatibilityInfo(
                        nameof(SuppressPluginUpdate),
                        false,
                        "InstallationManager type not found in assembly");
                    return;
                }
                
                // 查找 GetAvailablePluginUpdates 方法，可能有不同的访问级别或参数
                var methods = installationManager.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.Name == "GetAvailablePluginUpdates")
                    .ToArray();
                
                if (methods.Length > 0)
                {
                    // 优先选择返回 Task<PackageVersionInfo[]> 的方法
                    _getAvailablePluginUpdates = methods.FirstOrDefault(m => 
                        m.ReturnType == typeof(Task<PackageVersionInfo[]>)) ?? methods[0];
                    
                    Plugin.Instance.Logger.Info($"{nameof(SuppressPluginUpdate)}: Found GetAvailablePluginUpdates method");
                    Plugin.Instance.Logger.Info($"  Return type: {_getAvailablePluginUpdates.ReturnType.Name}");
                    Plugin.Instance.Logger.Info($"  Parameters: {_getAvailablePluginUpdates.GetParameters().Length}");
                    
                    EmbyVersionCompatibility.LogCompatibilityInfo(
                        nameof(SuppressPluginUpdate),
                        true,
                        "All components loaded successfully");
                }
                else
                {
                    Plugin.Instance.Logger.Warn($"{nameof(SuppressPluginUpdate)}: GetAvailablePluginUpdates method not found");
                    
                    // 列出所有可能相关的方法
                    var allMethods = installationManager.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(m => m.Name.Contains("Plugin") || m.Name.Contains("Update") || m.Name.Contains("Available"))
                        .Select(m => $"{m.Name}({m.GetParameters().Length} params) -> {m.ReturnType.Name}")
                        .ToArray();
                    
                    if (allMethods.Length > 0)
                    {
                        Plugin.Instance.Logger.Info($"{nameof(SuppressPluginUpdate)}: Available related methods in InstallationManager:");
                        foreach (var method in allMethods)
                        {
                            Plugin.Instance.Logger.Info($"  - {method}");
                        }
                    }
                    
                    PatchTracker.FallbackPatchApproach = PatchApproach.None;
                    
                    EmbyVersionCompatibility.LogCompatibilityInfo(
                        nameof(SuppressPluginUpdate),
                        false,
                        "GetAvailablePluginUpdates method not found");
                }
            }
            catch (Exception ex)
            {
                Plugin.Instance.Logger.Error($"{nameof(SuppressPluginUpdate)} initialization failed: {ex.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    Plugin.Instance.Logger.Debug($"Exception type: {ex.GetType().Name}");
                    Plugin.Instance.Logger.Debug(ex.StackTrace);
                }
                
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                
                EmbyVersionCompatibility.LogCompatibilityInfo(
                    nameof(SuppressPluginUpdate),
                    false,
                    "Initialization error - feature disabled");
            }
        }
        protected override void Prepare(bool apply)
        {
            if (_getAvailablePluginUpdates == null)
            {
                Plugin.Instance.Logger.Warn($"{nameof(SuppressPluginUpdate)}: Cannot patch - method not available");
                return;
            }
            
            PatchUnpatch(PatchTracker, apply, _getAvailablePluginUpdates,
                postfix: nameof(GetAvailablePluginUpdatesPostfix));
        }

        [HarmonyPostfix]
        private static Task<PackageVersionInfo[]> GetAvailablePluginUpdatesPostfix(Task<PackageVersionInfo[]> __result)
        {
            PackageVersionInfo[] result = null;

            try
            {
                result = __result?.Result;
            }
            catch
            {
                // ignored
            }

            if (result is null) return Task.FromResult(Array.Empty<PackageVersionInfo>());

            var suppressPluginUpdates = new HashSet<string>(
                Plugin.Instance.ExperienceEnhanceStore.GetOptions().SuppressPluginUpdates
                    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim()), StringComparer.OrdinalIgnoreCase);

            result = result.Where(p =>
                    !suppressPluginUpdates.Contains(p.name) &&
                    !suppressPluginUpdates.Contains(Path.GetFileNameWithoutExtension(p.targetFilename)))
                .ToArray();

            return Task.FromResult(result);
        }
    }
}
