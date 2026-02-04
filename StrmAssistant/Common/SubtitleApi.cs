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
                        
                        var methods = subtitleResolverType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            .Where(m => m.Name == "GetExternalSubtitleStreams" || 
                                       m.Name == "GetExternalStreams" || 
                                       m.Name == "GetExternalTracks")
                            .ToArray();
                        
                        if (methods.Length > 0)
                        {
                            _getExternalSubtitleStreams = methods.OrderByDescending(m => m.GetParameters().Length).First();
                            
                            var paramCount = _getExternalSubtitleStreams.GetParameters().Length;
                            var paramTypes = string.Join(", ", _getExternalSubtitleStreams.GetParameters().Select(p => p.ParameterType.Name));
                            _logger.Info($"{nameof(SubtitleApi)}: Found {_getExternalSubtitleStreams.Name} with {paramCount} parameters: {paramTypes}");
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
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Error($"{nameof(SubtitleApi)} - Failed to initialize reflection components: {e.Message}");
            }

            if (_subtitleResolver is null || _getExternalSubtitleStreams is null ||
                _ffProbeSubtitleInfo is null || _updateExternalSubtitleStream is null)
            {
                PatchTracker.FallbackPatchApproach = PatchApproach.Reflection;
                _logger.Info($"{nameof(SubtitleApi)} - Using fallback approach.");
            }
            else if (Plugin.Instance.IsModSupported)
            {
                // 执行 Harmony 逆向打补丁
                var patch1 = PatchManager.ReversePatch(PatchTracker, _getExternalSubtitleStreams,
                    nameof(GetExternalSubtitleStreamsStub));
                var patch2 = PatchManager.ReversePatch(PatchTracker, _updateExternalSubtitleStream,
                    nameof(UpdateExternalSubtitleStreamStub));
                
                if ((patch1 || patch2) && PatchTracker.FallbackPatchApproach == PatchApproach.Harmony)
                {
                    _logger.Info($"{nameof(SubtitleApi)} - Harmony patches applied successfully (Target: Emby 4.9.1+)");
                }
            }
        }

        // --- 核心修改点 1: Stub 增加 LibraryOptions ---
        [HarmonyReversePatch]
        private static List<MediaStream> GetExternalSubtitleStreamsStub(object instance, BaseItem item, int startIndex,
            IDirectoryService directoryService, LibraryOptions libraryOptions, NamingOptions namingOptions, bool clearCache) =>
            throw new NotImplementedException();

        private List<MediaStream> GetExternalSubtitleStreams(BaseItem item, int startIndex,
            IDirectoryService directoryService, bool clearCache)
        {
            var namingOptions = _libraryManager.GetNamingOptions();

            switch (PatchTracker.FallbackPatchApproach)
            {
                // --- 核心修改点 2: 调用时传入 LibraryOptions ---
                case PatchApproach.Harmony:
                    try
                    {
                        var libraryOptions = _libraryManager.GetLibraryOptions(item);
                        return GetExternalSubtitleStreamsStub(_subtitleResolver, item, startIndex, directoryService,
                            libraryOptions, namingOptions, clearCache);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Harmony stub failed in GetExternalSubtitleStreams: {ex.Message}");
                        PatchTracker.FallbackPatchApproach = PatchApproach.Reflection;
                        return GetExternalSubtitleStreams(item, startIndex, directoryService, clearCache);
                    }
                    
                case PatchApproach.Reflection:
                    if (_subtitleResolver == null || _getExternalSubtitleStreams == null) return new List<MediaStream>();
                    try
                    {
                        var paramCount = _getExternalSubtitleStreams.GetParameters().Length;
                        object[] args;
                        
                        if (paramCount == 6)
                        {
                            var libraryOptions = _libraryManager.GetLibraryOptions(item);
                            args = new object[] { item, startIndex, directoryService, libraryOptions, namingOptions, clearCache };
                        }
                        else
                        {
                            args = new object[] { item, startIndex, directoryService, namingOptions, clearCache };
                        }
                        
                        var result = _getExternalSubtitleStreams.Invoke(_subtitleResolver, args);
                        return result as List<MediaStream> ?? new List<MediaStream>();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Reflection failed: {ex.Message}");
                        return new List<MediaStream>();
                    }
                    
                default:
                    return new List<MediaStream>();
            }
        }

        [HarmonyReversePatch]
        private static async Task<bool> UpdateExternalSubtitleStreamStub(object instance, BaseItem item,
            MediaStream subtitleStream, MetadataRefreshOptions options, LibraryOptions libraryOptions,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

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
                        PatchTracker.FallbackPatchApproach = PatchApproach.Reflection;
                        return UpdateExternalSubtitleStream(item, subtitleStream, options, cancellationToken);
                    }
                    
                case PatchApproach.Reflection:
                    if (_ffProbeSubtitleInfo == null || _updateExternalSubtitleStream == null) return Task.FromResult(false);
                    try
                    {
                        var result = _updateExternalSubtitleStream.Invoke(_ffProbeSubtitleInfo,
                            new object[] { item, subtitleStream, options, libraryOptions, cancellationToken });
                        return result as Task<bool> ?? Task.FromResult(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Reflection failed in UpdateExternalSubtitleStream: {ex.Message}");
                        return Task.FromResult(false);
                    }
                default:
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
            catch { return false; }
        }

        public async Task UpdateExternalSubtitles(BaseItem item, MetadataRefreshOptions refreshOptions, bool clearCache,
            bool persistMediaInfo)
        {
            var directoryService = refreshOptions.DirectoryService;
            var currentStreams = item.GetMediaStreams()
                .FindAll(i => !(i.IsExternal && i.Type == MediaStreamType.Subtitle && i.Protocol == MediaProtocol.File));
            var startIndex = currentStreams.Count == 0 ? 0 : currentStreams.Max(i => i.Index) + 1;

            if (GetExternalSubtitleStreams(item, startIndex, directoryService, clearCache) is { } externalSubtitleStreams)
            {
                foreach (var subtitleStream in externalSubtitleStreams)
                {
                    var extension = Path.GetExtension(subtitleStream.Path);
                    if (!string.IsNullOrEmpty(extension) && ProbeExtensions.Contains(extension))
                    {
                        await UpdateExternalSubtitleStream(item, subtitleStream, refreshOptions, CancellationToken.None).ConfigureAwait(false);
                    }
                }

                currentStreams.AddRange(externalSubtitleStreams);
                _itemRepository.SaveMediaStreams(item.InternalId, currentStreams, CancellationToken.None);

                if (persistMediaInfo && Plugin.LibraryApi.IsLibraryInScope(item))
                {
                    _ = Plugin.MediaInfoApi.SerializeMediaInfo(item.InternalId, directoryService, true, "External Subtitle Update").ConfigureAwait(false);
                }
            }
        }
    }
}