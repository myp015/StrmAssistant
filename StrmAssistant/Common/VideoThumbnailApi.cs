using HarmonyLib;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using StrmAssistant.Mod;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace StrmAssistant.Common
{
    public class VideoThumbnailApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;

        private static readonly PatchTracker PatchTracker = new PatchTracker(typeof(VideoThumbnailApi),
            Plugin.Instance.IsModSupported ? PatchApproach.Harmony : PatchApproach.Reflection);

        private readonly object _thumbnailGenerator;
        private readonly MethodInfo _refreshThumbnailImages;

        private static readonly Version AppVer = Plugin.Instance.ApplicationHost.ApplicationVersion;
        private static readonly Version Ver4936 = new Version("4.9.0.36");

        public VideoThumbnailApi(ILibraryManager libraryManager, IFileSystem fileSystem,
            IImageExtractionManager imageExtractionManager, IItemRepository itemRepository,
            IMediaMountManager mediaMountManager, IServerApplicationPaths applicationPaths,
            ILibraryMonitor libraryMonitor, IFfmpegManager ffmpegManager)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;

            try
            {
                var embyProviders = EmbyVersionCompatibility.TryLoadAssembly("Emby.Providers");
                if (embyProviders == null)
                {
                    _logger.Error($"{nameof(VideoThumbnailApi)} - Failed to load Emby.Providers assembly");
                    PatchTracker.FallbackPatchApproach = PatchApproach.None;
                    return;
                }

                var thumbnailGenerator = EmbyVersionCompatibility.TryGetType(embyProviders, "Emby.Providers.MediaInfo.ThumbnailGenerator");
                if (thumbnailGenerator != null)
                {
                    var thumbnailGeneratorConstructor = thumbnailGenerator.GetConstructor(
                        BindingFlags.Public | BindingFlags.Instance, null,
                        new[]
                        {
                            typeof(IFileSystem), typeof(ILogger), typeof(IImageExtractionManager),
                            typeof(IItemRepository), typeof(IMediaMountManager), typeof(IServerApplicationPaths),
                            typeof(ILibraryMonitor), typeof(IFfmpegManager)
                        }, null);
                    
                    if (thumbnailGeneratorConstructor != null)
                    {
                        _thumbnailGenerator = thumbnailGeneratorConstructor.Invoke(new object[]
                        {
                            fileSystem, _logger, imageExtractionManager, itemRepository, mediaMountManager,
                            applicationPaths, libraryMonitor, ffmpegManager
                        });
                        
                        // 查找 RefreshThumbnailImages 方法，可能有多个重载
                        var refreshMethods = thumbnailGenerator.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => m.Name == "RefreshThumbnailImages")
                            .ToArray();
                        
                        if (refreshMethods.Length > 0)
                        {
                            // 优先选择参数最多的版本
                            _refreshThumbnailImages = refreshMethods.OrderByDescending(m => m.GetParameters().Length).First();
                            
                            var paramCount = _refreshThumbnailImages.GetParameters().Length;
                            var paramTypes = string.Join(", ", _refreshThumbnailImages.GetParameters().Select(p => p.ParameterType.Name));
                            _logger.Info($"{nameof(VideoThumbnailApi)}: Found RefreshThumbnailImages with {paramCount} parameters: {paramTypes}");
                        }
                        else
                        {
                            _logger.Warn($"{nameof(VideoThumbnailApi)}: RefreshThumbnailImages method not found");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Error($"{nameof(VideoThumbnailApi)} - Initialization failed: {e.Message}");
                if (Plugin.Instance.DebugMode)
                {
                    _logger.Debug($"Exception type: {e.GetType().Name}");
                    _logger.Debug(e.StackTrace);
                }
            }

            if (_thumbnailGenerator is null || _refreshThumbnailImages is null)
            {
                var missingComponents = new List<string>();
                if (_thumbnailGenerator == null) missingComponents.Add("ThumbnailGenerator");
                if (_refreshThumbnailImages == null) missingComponents.Add("RefreshThumbnailImages");

                _logger.Warn($"{nameof(VideoThumbnailApi)} - Missing components: {string.Join(", ", missingComponents)}");
                _logger.Info($"{nameof(VideoThumbnailApi)} - Video thumbnail extraction not available on this Emby version");
                
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                
                EmbyVersionCompatibility.LogCompatibilityInfo(
                    nameof(VideoThumbnailApi),
                    false,
                    "ThumbnailGenerator not available");
            }
            else if (Plugin.Instance.IsModSupported)
            {
                // 根据参数数量选择合适的stub
                string stubName;
                var paramCount = _refreshThumbnailImages?.GetParameters().Length ?? 0;
                
                if (paramCount == 10)
                {
                    stubName = nameof(RefreshThumbnailImagesStub491); // Emby 4.9.1.x+
                }
                else if (paramCount == 9 || AppVer >= Ver4936)
                {
                    stubName = nameof(RefreshThumbnailImagesStub49); // Emby 4.9.0.36+
                }
                else
                {
                    stubName = nameof(RefreshThumbnailImagesStub48); // Emby 4.8.x
                }
                
                var patchSuccess = PatchManager.ReversePatch(PatchTracker, _refreshThumbnailImages, stubName);
                
                if (patchSuccess && PatchTracker.FallbackPatchApproach == PatchApproach.Harmony)
                {
                    _logger.Info($"{nameof(VideoThumbnailApi)} - Harmony patch applied (stub: {stubName}, params: {paramCount})");
                }
                
                EmbyVersionCompatibility.LogCompatibilityInfo(
                    nameof(VideoThumbnailApi),
                    true,
                    $"Using {PatchTracker.FallbackPatchApproach} approach for Emby {AppVer}");
            }
            else
            {
                _logger.Info($"{nameof(VideoThumbnailApi)} - Reflection approach active");
                EmbyVersionCompatibility.LogCompatibilityInfo(
                    nameof(VideoThumbnailApi),
                    true,
                    "Reflection mode active");
            }
        }

#pragma warning disable CS1998
        [HarmonyReversePatch]
        private static async Task<bool> RefreshThumbnailImagesStub491(object instance, Video item,
            MediaSourceInfo mediaSource, MediaStream videoStream, LibraryOptions libraryOptions,
            IDirectoryService directoryService, List<ChapterInfo> chapters, bool extractImages, bool saveChapters,
            bool forceRefresh, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        [HarmonyReversePatch]
        private static async Task<bool> RefreshThumbnailImagesStub49(object instance, Video item,
            MediaSourceInfo mediaSource, MediaStream videoStream, LibraryOptions libraryOptions,
            IDirectoryService directoryService, List<ChapterInfo> chapters, bool extractImages, bool saveChapters,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        [HarmonyReversePatch]
        private static async Task<bool> RefreshThumbnailImagesStub48(object instance, Video item, MediaStream videoStream,
            LibraryOptions libraryOptions, IDirectoryService directoryService, List<ChapterInfo> chapters,
            bool extractImages, bool saveChapters, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
#pragma warning restore CS1998

        public Task<bool> RefreshThumbnailImages(Video item, LibraryOptions libraryOptions,
            IDirectoryService directoryService, List<ChapterInfo> chapters, bool extractImages, bool saveChapters,
            CancellationToken cancellationToken)
        {
            var mediaSource = AppVer >= Ver4936
                ? item.GetMediaSources(false, false, libraryOptions).FirstOrDefault()
                : null;
            
            var paramCount = _refreshThumbnailImages?.GetParameters().Length ?? 0;

            switch (PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    try
                    {
                        if (paramCount == 10)
                        {
                            // Emby 4.9.1.x+: 10 参数版本
                            return RefreshThumbnailImagesStub491(_thumbnailGenerator, item, mediaSource, null, libraryOptions,
                                directoryService, chapters, extractImages, saveChapters, false, cancellationToken);
                        }
                        else if (paramCount == 9 || AppVer >= Ver4936)
                        {
                            // Emby 4.9.0.36+: 9 参数版本
                            return RefreshThumbnailImagesStub49(_thumbnailGenerator, item, mediaSource, null, libraryOptions,
                                directoryService, chapters, extractImages, saveChapters, cancellationToken);
                        }
                        else
                        {
                            // Emby 4.8.x: 8 参数版本
                            return RefreshThumbnailImagesStub48(_thumbnailGenerator, item, null, libraryOptions,
                                directoryService, chapters, extractImages, saveChapters, cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Harmony stub failed in RefreshThumbnailImages: {ex.Message}");
                        if (Plugin.Instance.DebugMode)
                        {
                            _logger.Debug(ex.StackTrace);
                        }
                        PatchTracker.FallbackPatchApproach = PatchApproach.Reflection;
                        return RefreshThumbnailImages(item, libraryOptions, directoryService, chapters, extractImages, saveChapters, cancellationToken);
                    }
                    
                case PatchApproach.Reflection:
                {
                    if (_thumbnailGenerator == null || _refreshThumbnailImages == null)
                    {
                        _logger.Warn("ThumbnailGenerator not available");
                        return Task.FromResult(false);
                    }
                    
                    try
                    {
                        // 根据参数数量动态构造参数
                        object[] parameters;
                        
                        if (paramCount == 10)
                        {
                            // Emby 4.9.1.x+: (Video, MediaSourceInfo, MediaStream, LibraryOptions, IDirectoryService, List<ChapterInfo>, bool, bool, bool, CancellationToken)
                            parameters = new object[]
                            {
                                item, mediaSource, null, libraryOptions, directoryService, chapters, extractImages,
                                saveChapters, false, cancellationToken
                            };
                        }
                        else if (paramCount == 9 || AppVer >= Ver4936)
                        {
                            // Emby 4.9.0.36+: (Video, MediaSourceInfo, MediaStream, LibraryOptions, IDirectoryService, List<ChapterInfo>, bool, bool, CancellationToken)
                            parameters = new object[]
                            {
                                item, mediaSource, null, libraryOptions, directoryService, chapters, extractImages,
                                saveChapters, cancellationToken
                            };
                        }
                        else
                        {
                            // Emby 4.8.x: (Video, MediaStream, LibraryOptions, IDirectoryService, List<ChapterInfo>, bool, bool, CancellationToken)
                            parameters = new object[]
                            {
                                item, null, libraryOptions, directoryService, chapters, extractImages, saveChapters,
                                cancellationToken
                            };
                        }

                        var result = _refreshThumbnailImages.Invoke(_thumbnailGenerator, parameters);
                        return result as Task<bool> ?? Task.FromResult(false);
                    }
                    catch (TargetInvocationException tie)
                    {
                        var innerEx = tie.InnerException ?? tie;
                        _logger.Error($"Failed to invoke RefreshThumbnailImages: {innerEx.Message}");
                        if (Plugin.Instance.DebugMode)
                        {
                            _logger.Debug(innerEx.StackTrace);
                        }
                        return Task.FromResult(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Reflection failed in RefreshThumbnailImages: {ex.Message}");
                        if (Plugin.Instance.DebugMode)
                        {
                            _logger.Debug(ex.StackTrace);
                        }
                        return Task.FromResult(false);
                    }
                }
                default:
                    _logger.Warn("RefreshThumbnailImages: Feature not available");
                    return Task.FromResult(false);
            }
        }

        public List<Video> FetchExtractTaskItems()
        {
            var libraryIds = Plugin.Instance.MediaInfoExtractStore.GetOptions()
                .LibraryScope.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
            var librariesWithVideoThumbnail = _libraryManager.GetVirtualFolders()
                .Where(f => f.LibraryOptions.EnableChapterImageExtraction)
                .ToList();
            var librariesSelected = librariesWithVideoThumbnail.Where(f => libraryIds.Contains(f.Id)).ToList();

            _logger.Info("VideoThumbnailExtract - LibraryScope: " + (!librariesWithVideoThumbnail.Any()
                ? "NONE"
                : string.Join(", ",
                    (libraryIds.Contains("-1")
                        ? new[] { Resources.Favorites }.Concat(librariesSelected.Select(l => l.Name))
                        : librariesSelected.Select(l => l.Name)).DefaultIfEmpty("ALL"))));

            var includeExtra = Plugin.Instance.MediaInfoExtractStore.GetOptions().IncludeExtra;
            _logger.Info("Include Extra: " + includeExtra);

            var librariesWithVideoThumbnailPaths = librariesWithVideoThumbnail.SelectMany(l => l.Locations)
                .Select(ls =>
                    ls.EndsWith(Path.DirectorySeparatorChar.ToString()) ? ls : ls + Path.DirectorySeparatorChar)
                .ToArray();
            var librariesSelectedPaths = librariesSelected.SelectMany(l => l.Locations)
                .Select(ls =>
                    ls.EndsWith(Path.DirectorySeparatorChar.ToString()) ? ls : ls + Path.DirectorySeparatorChar)
                .ToArray();

            var favoritesWithExtra = Array.Empty<BaseItem>();

            if (libraryIds.Contains("-1") && librariesWithVideoThumbnailPaths.Any())
            {
                var favorites = LibraryApi.AllUsers.Select(e => e.Key)
                    .SelectMany(user => _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        User = user,
                        IsFavorite = true,
                        PathStartsWithAny = librariesWithVideoThumbnailPaths
                    })).GroupBy(i => i.InternalId).Select(g => g.First()).ToList();

                var expanded = Plugin.LibraryApi.ExpandFavorites(favorites, false, false, false);

                favoritesWithExtra = expanded
                    .Concat(includeExtra
                        ? expanded.SelectMany(f => f.GetExtras(LibraryApi.IncludeExtraTypes))
                        : Enumerable.Empty<BaseItem>())
                    .Where(i => Plugin.LibraryApi.HasMediaInfo(i) && !i.HasImage(ImageType.Chapter))
                    .ToArray();
            }

            var items = Array.Empty<BaseItem>();
            var extras = Array.Empty<BaseItem>();

            if (!libraryIds.Any() && librariesWithVideoThumbnailPaths.Any() ||
                libraryIds.Any(id => id != "-1") && librariesSelectedPaths.Any())
            {
                var videoThumbnailQuery = new InternalItemsQuery
                {
                    MediaTypes = new[] { MediaType.Video },
                    HasAudioStream = true,
                    HasChapterImages = false,
                    PathStartsWithAny = !libraryIds.Any() ? librariesWithVideoThumbnailPaths : librariesSelectedPaths
                };

                items = _libraryManager.GetItemList(videoThumbnailQuery);

                if (includeExtra)
                {
                    videoThumbnailQuery.ExtraTypes = LibraryApi.IncludeExtraTypes;
                    extras = _libraryManager.GetItemList(videoThumbnailQuery);
                }
            }

            var isModSupported = Plugin.Instance.IsModSupported;
            var combined = favoritesWithExtra.Concat(items).Concat(extras).GroupBy(i => i.InternalId)
                .Select(g => g.First()).Where(i => isModSupported || !i.IsShortcut).OfType<Video>().ToList();

            return combined;
        }
    }
}
