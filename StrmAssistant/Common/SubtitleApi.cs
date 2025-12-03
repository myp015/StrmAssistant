using Emby.Naming.Common;
using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using StrmAssistant.Core;
using StrmAssistant.Mod;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    public class SubtitleApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IFileSystem _fileSystem;

        private static readonly PatchTracker PatchTracker =
            new PatchTracker(typeof(SubtitleApi),
                Plugin.Instance.IsModSupported ? PatchApproach.Harmony : PatchApproach.Reflection);
        private readonly object _subtitleResolver;
        private readonly MethodInfo _getExternalSubtitleStreams;
        private readonly object _ffProbeSubtitleInfo;
        private readonly MethodInfo _updateExternalSubtitleStream;

        private static readonly HashSet<string> ProbeExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".sub", ".smi", ".sami", ".mpl" };

        public SubtitleApi(ILibraryManager libraryManager, IFileSystem fileSystem, IMediaProbeManager mediaProbeManager,
            ILocalizationManager localizationManager, IItemRepository itemRepository)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _fileSystem = fileSystem;

            try
            {
                var embyProviders = EmbyVersionAdapter.Instance.TryLoadAssembly("Emby.Providers");
                if (embyProviders == null)
                {
                    _logger.Error($"{nameof(SubtitleApi)} - Failed to load Emby.Providers assembly");
                    PatchTracker.FallbackPatchApproach = PatchApproach.None;
                    return;
                }

                var subtitleResolverType = EmbyVersionAdapter.Instance.TryGetType("Emby.Providers", "Emby.Providers.MediaInfo.SubtitleResolver");
                if (subtitleResolverType != null)
                {
                    var subtitleResolverConstructor = subtitleResolverType.GetConstructor(new[]
                    {
                        typeof(ILocalizationManager), typeof(IFileSystem), typeof(ILibraryManager)
                    });
                    
                    if (subtitleResolverConstructor != null)
                    {
                        _subtitleResolver = subtitleResolverConstructor.Invoke(new object[]
                        {
                            localizationManager, fileSystem, libraryManager
                        });
                        
                        // 尝试查找字幕相关方法，可能有多个重载或不同名称
                        // Emby 4.8.x-4.9.0.x: GetExternalSubtitleStreams
                        // Emby 4.9.1.x+: GetExternalTracks
                        var methods = subtitleResolverType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            .Where(m => m.Name == "GetExternalSubtitleStreams" || 
                                       m.Name == "GetExternalStreams" || 
                                       m.Name == "GetExternalTracks")
                            .ToArray();
                        
                        if (methods.Length > 0)
                        {
                            // 优先选择参数最多的版本（通常是最新的）
                            _getExternalSubtitleStreams = methods.OrderByDescending(m => m.GetParameters().Length).First();
                            
                            var paramCount = _getExternalSubtitleStreams.GetParameters().Length;
                            var paramTypes = string.Join(", ", _getExternalSubtitleStreams.GetParameters().Select(p => p.ParameterType.Name));
                            _logger.Info($"{nameof(SubtitleApi)}: Found {_getExternalSubtitleStreams.Name} with {paramCount} parameters: {paramTypes}");
                        }
                        else
                        {
                            // 尝试查找所有可能相关的方法
                            var allMethods = subtitleResolverType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                                .Where(m => m.Name.Contains("Subtitle") || m.Name.Contains("External") || m.Name.Contains("Stream"))
                                .Select(m => {
                                    var parameters = m.GetParameters();
                                    var paramStr = string.Join(", ", parameters.Select(p => p.ParameterType.Name));
                                    return $"{m.Name}({paramStr})";
                                })
                                .ToArray();
                            
                            // 总是输出可用方法以便调试
                            if (allMethods.Length > 0)
                            {
                                _logger.Info($"{nameof(SubtitleApi)}: Available methods in SubtitleResolver:");
                                foreach (var method in allMethods)
                                {
                                    _logger.Info($"  - {method}");
                                }
                            }
                            else
                            {
                                _logger.Warn($"{nameof(SubtitleApi)}: No subtitle-related methods found in SubtitleResolver");
                            }
                            
                            _logger.Warn($"{nameof(SubtitleApi)}: Emby's internal GetExternalSubtitleStreams method not found in SubtitleResolver - external subtitle scanning may be limited");
                        }
                        
                        if (Plugin.Instance.DebugMode && _subtitleResolver != null)
                        {
                            _logger.Debug($"{nameof(SubtitleApi)}: SubtitleResolver initialized successfully");
                        }
                    }
                }

                var ffProbeSubtitleInfoType = EmbyVersionAdapter.Instance.TryGetType("Emby.Providers", "Emby.Providers.MediaInfo.FFProbeSubtitleInfo");
                if (ffProbeSubtitleInfoType != null)
                {
                    var ffProbeSubtitleInfoConstructor = ffProbeSubtitleInfoType.GetConstructor(new[]
                    {
                        typeof(IMediaProbeManager)
                    });
                    
                    if (ffProbeSubtitleInfoConstructor != null)
                    {
                        _ffProbeSubtitleInfo = ffProbeSubtitleInfoConstructor.Invoke(new object[] { mediaProbeManager });
                        _updateExternalSubtitleStream = ffProbeSubtitleInfoType.GetMethod("UpdateExternalSubtitleStream");
                        
                        if (Plugin.Instance.DebugMode && _updateExternalSubtitleStream != null)
                        {
                            _logger.Debug($"{nameof(SubtitleApi)}: FFProbeSubtitleInfo initialized successfully");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Error($"{nameof(SubtitleApi)} - Failed to initialize reflection components: {e.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    _logger.Debug($"Exception type: {e.GetType().Name}");
                    _logger.Debug(e.StackTrace);
                }
            }

            if (_subtitleResolver is null || _getExternalSubtitleStreams is null ||
                _ffProbeSubtitleInfo is null || _updateExternalSubtitleStream is null)
            {
                _logger.Warn($"{nameof(SubtitleApi)} - Some reflection methods not available");
                
                // 检查哪些组件不可用
                if (_subtitleResolver == null) _logger.Warn("  - SubtitleResolver not initialized");
                if (_getExternalSubtitleStreams == null) _logger.Warn("  - Emby's SubtitleResolver.GetExternalSubtitleStreams method not found");
                if (_ffProbeSubtitleInfo == null) _logger.Warn("  - FFProbeSubtitleInfo not initialized");
                if (_updateExternalSubtitleStream == null) _logger.Warn("  - FFProbeSubtitleInfo.UpdateExternalSubtitleStream method not found");
                
                // 外挂字幕扫描功能可能仍然可用（通过公共API或部分功能），标记为Reflection而不是None
                PatchTracker.FallbackPatchApproach = PatchApproach.Reflection;
                _logger.Info($"{nameof(SubtitleApi)} - Using fallback approach. Some features may be limited.");
                
                EmbyVersionAdapter.Instance.LogCompatibilityInfo(
                    nameof(SubtitleApi),
                    false,
                    "Some internal APIs not found - fallback mode active");
            }
            else if (Plugin.Instance.IsModSupported)
            {
                var patch1 = PatchManager.ReversePatch(PatchTracker, _getExternalSubtitleStreams,
                    nameof(GetExternalSubtitleStreamsStub));
                var patch2 = PatchManager.ReversePatch(PatchTracker, _updateExternalSubtitleStream,
                    nameof(UpdateExternalSubtitleStreamStub));
                
                if ((patch1 || patch2) && PatchTracker.FallbackPatchApproach == PatchApproach.Harmony)
                {
                    _logger.Info($"{nameof(SubtitleApi)} - Harmony patches applied successfully");
                }
                
                EmbyVersionAdapter.Instance.LogCompatibilityInfo(
                    nameof(SubtitleApi),
                    true,
                    $"Using {PatchTracker.FallbackPatchApproach} approach");
            }
            else
            {
                _logger.Info($"{nameof(SubtitleApi)} - Reflection approach active");
                EmbyVersionAdapter.Instance.LogCompatibilityInfo(
                    nameof(SubtitleApi),
                    true,
                    "Reflection mode (Harmony not supported on platform)");
            }
        }

        [HarmonyReversePatch]
        private static List<MediaStream> GetExternalSubtitleStreamsStub(object instance, BaseItem item, int startIndex,
            IDirectoryService directoryService, NamingOptions namingOptions, bool clearCache) =>
            throw new NotImplementedException();

        private List<MediaStream> GetExternalSubtitleStreams(BaseItem item, int startIndex,
            IDirectoryService directoryService, bool clearCache)
        {
            var namingOptions = _libraryManager.GetNamingOptions();

            switch (PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    try
                    {
                        return GetExternalSubtitleStreamsStub(_subtitleResolver, item, startIndex, directoryService,
                            namingOptions, clearCache);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Harmony stub failed in GetExternalSubtitleStreams: {ex.Message}");
                        if (Plugin.Instance.DebugMode)
                        {
                            _logger.Debug(ex.StackTrace);
                        }
                        // 降级到反射
                        PatchTracker.FallbackPatchApproach = PatchApproach.Reflection;
                        return GetExternalSubtitleStreams(item, startIndex, directoryService, clearCache);
                    }
                    
                case PatchApproach.Reflection:
                    if (_subtitleResolver == null || _getExternalSubtitleStreams == null)
                    {
                        _logger.Warn("Subtitle resolver not available, returning empty list");
                        return new List<MediaStream>();
                    }
                    
                    try
                    {
                        // 根据方法参数数量动态构造参数
                        var paramCount = _getExternalSubtitleStreams.GetParameters().Length;
                        object[] args;
                        
                        if (paramCount == 6)
                        {
                            // Emby 4.9.1.x+: GetExternalTracks(BaseItem, Int32, IDirectoryService, LibraryOptions, NamingOptions, Boolean)
                            var libraryOptions = _libraryManager.GetLibraryOptions(item);
                            args = new object[] { item, startIndex, directoryService, libraryOptions, namingOptions, clearCache };
                        }
                        else
                        {
                            // Emby 4.8.x-4.9.0.x: GetExternalSubtitleStreams(BaseItem, Int32, IDirectoryService, NamingOptions, Boolean)
                            args = new object[] { item, startIndex, directoryService, namingOptions, clearCache };
                        }
                        
                        var result = _getExternalSubtitleStreams.Invoke(_subtitleResolver, args);
                        return result as List<MediaStream> ?? new List<MediaStream>();
                    }
                    catch (TargetInvocationException tie)
                    {
                        var innerEx = tie.InnerException ?? tie;
                        _logger.Error($"Failed to invoke GetExternalSubtitleStreams: {innerEx.Message}");
                        if (Plugin.Instance.DebugMode)
                        {
                            _logger.Debug(innerEx.StackTrace);
                        }
                        return new List<MediaStream>();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Reflection failed in GetExternalSubtitleStreams: {ex.Message}");
                        if (Plugin.Instance.DebugMode)
                        {
                            _logger.Debug(ex.StackTrace);
                        }
                        return new List<MediaStream>();
                    }
                    
                default:
                    _logger.Warn("GetExternalSubtitleStreams: Unknown patch approach, returning empty list");
                    return new List<MediaStream>();
            }
        }

#pragma warning disable CS1998
        [HarmonyReversePatch]
        private static async Task<bool> UpdateExternalSubtitleStreamStub(object instance, BaseItem item,
            MediaStream subtitleStream, MetadataRefreshOptions options, LibraryOptions libraryOptions,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();
#pragma warning restore CS1998

        private Task<bool> UpdateExternalSubtitleStream(BaseItem item, MediaStream subtitleStream,
            MetadataRefreshOptions options, CancellationToken cancellationToken)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            switch (PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    try
                    {
                        return UpdateExternalSubtitleStreamStub(_ffProbeSubtitleInfo, item, subtitleStream, options,
                            libraryOptions, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Harmony stub failed in UpdateExternalSubtitleStream: {ex.Message}");
                        if (Plugin.Instance.DebugMode)
                        {
                            _logger.Debug(ex.StackTrace);
                        }
                        // 降级到反射
                        PatchTracker.FallbackPatchApproach = PatchApproach.Reflection;
                        return UpdateExternalSubtitleStream(item, subtitleStream, options, cancellationToken);
                    }
                    
                case PatchApproach.Reflection:
                    if (_ffProbeSubtitleInfo == null || _updateExternalSubtitleStream == null)
                    {
                        _logger.Warn("FFProbe subtitle info not available, returning false");
                        return Task.FromResult(false);
                    }
                    
                    try
                    {
                        var result = _updateExternalSubtitleStream.Invoke(_ffProbeSubtitleInfo,
                            new object[] { item, subtitleStream, options, libraryOptions, cancellationToken });
                        
                        if (result is Task<bool> taskResult)
                        {
                            return taskResult;
                        }
                        else
                        {
                            _logger.Warn($"Unexpected return type from UpdateExternalSubtitleStream: {result?.GetType().Name}");
                            return Task.FromResult(false);
                        }
                    }
                    catch (TargetInvocationException tie)
                    {
                        var innerEx = tie.InnerException ?? tie;
                        _logger.Error($"Failed to invoke UpdateExternalSubtitleStream: {innerEx.Message}");
                        if (Plugin.Instance.DebugMode)
                        {
                            _logger.Debug(innerEx.StackTrace);
                        }
                        return Task.FromResult(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Reflection failed in UpdateExternalSubtitleStream: {ex.Message}");
                        if (Plugin.Instance.DebugMode)
                        {
                            _logger.Debug(ex.StackTrace);
                        }
                        return Task.FromResult(false);
                    }
                    
                default:
                    _logger.Warn("UpdateExternalSubtitleStream: Unknown patch approach, returning false");
                    return Task.FromResult(false);
            }
        }

        public MetadataRefreshOptions GetExternalSubtitleRefreshOptions()
        {
            return new MetadataRefreshOptions(new DirectoryService(_logger, _fileSystem))
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllMetadata = false,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages = false,
                EnableThumbnailImageExtraction = false,
                EnableSubtitleDownloading = false
            };
        }

        public bool HasExternalSubtitleChanged(BaseItem item, IDirectoryService directoryService, bool clearCache)
        {
            // Emby 4.9.1.80+: GetExternalSubtitleFiles已移除，改用GetMediaStreams获取
            var currentExternalSubtitleFiles = item.GetMediaStreams()
                .Where(s => s.Type == MediaStreamType.Subtitle && s.IsExternal)
                .Select(s => s.Path)
                .ToArray();
            var currentSet = new HashSet<string>(currentExternalSubtitleFiles, StringComparer.Ordinal);

            try
            {
                var newExternalSubtitleFiles = GetExternalSubtitleStreams(item, 0, directoryService, clearCache)
                    .Select(i => i.Path)
                    .ToArray();
                var newSet = new HashSet<string>(newExternalSubtitleFiles, StringComparer.Ordinal);

                return !currentSet.SetEquals(newSet);
            }
            catch
            {
                // ignored
            }

            return false;
        }

        public async Task UpdateExternalSubtitles(BaseItem item, MetadataRefreshOptions refreshOptions, bool clearCache,
            bool persistMediaInfo)
        {
            var directoryService = refreshOptions.DirectoryService;
            var currentStreams = item.GetMediaStreams()
                .FindAll(i =>
                    !(i.IsExternal && i.Type == MediaStreamType.Subtitle && i.Protocol == MediaProtocol.File));
            var startIndex = currentStreams.Count == 0 ? 0 : currentStreams.Max(i => i.Index) + 1;

            if (GetExternalSubtitleStreams(item, startIndex, directoryService, clearCache) is
                { } externalSubtitleStreams)
            {
                foreach (var subtitleStream in externalSubtitleStreams)
                {
                    var extension = Path.GetExtension(subtitleStream.Path);
                    if (!string.IsNullOrEmpty(extension) && ProbeExtensions.Contains(extension))
                    {
                        var result =
                            await UpdateExternalSubtitleStream(item, subtitleStream, refreshOptions,
                                CancellationToken.None).ConfigureAwait(false);

                        if (!result)
                            _logger.Warn("No result when probing external subtitle file: {0}", subtitleStream.Path);
                    }

                    _logger.Info("ExternalSubtitle - Subtitle Processed: " + subtitleStream.Path);
                }

                currentStreams.AddRange(externalSubtitleStreams);
                _itemRepository.SaveMediaStreams(item.InternalId, currentStreams, CancellationToken.None);

                if (persistMediaInfo && Plugin.LibraryApi.IsLibraryInScope(item))
                {
                    _ = Plugin.MediaInfoApi.SerializeMediaInfo(item.InternalId, directoryService, true,
                        "External Subtitle Update").ConfigureAwait(false);
                }
            }
        }
    }
}
