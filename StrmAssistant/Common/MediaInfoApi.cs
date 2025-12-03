using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Serialization;
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
    public class MediaInfoApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IItemRepository _itemRepository;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IFileSystem _fileSystem;
        private readonly IProviderManager _providerManager;

        private const string MediaInfoFileExtension = "-mediainfo.json";

        private static readonly PatchTracker PatchTracker =
            new PatchTracker(typeof(MediaInfoApi),
                Plugin.Instance.IsModSupported ? PatchApproach.Harmony : PatchApproach.Reflection);

        private readonly bool _fallbackApproach;
        private readonly MethodInfo _getStaticMediaSources;

        internal class MediaSourceWithChapters
        {
            public MediaSourceInfo MediaSourceInfo { get; set; }
            public List<ChapterInfo> Chapters { get; set; } = new List<ChapterInfo>();
            public bool? ZeroFingerprintConfidence { get; set; }
            public string EmbeddedImage { get; set; }
        }

        public MediaInfoApi(ILibraryManager libraryManager, IFileSystem fileSystem, IProviderManager providerManager,
            IMediaSourceManager mediaSourceManager, IItemRepository itemRepository, IJsonSerializer jsonSerializer,
            ILibraryMonitor libraryMonitor)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _providerManager = providerManager;
            _mediaSourceManager = mediaSourceManager;
            _itemRepository = itemRepository;
            _jsonSerializer = jsonSerializer;

            if (EmbyVersionAdapter.Instance.IsVersionAtLeast(EmbyApiVersion.V4_9_0))
            {
                try
                {
                    var managerType = mediaSourceManager.GetType();
                    var currentVersion = EmbyVersionAdapter.Instance.CurrentVersion;
                    
                    // 使用EmbyVersionAdapter查找兼容的方法签名
                    _getStaticMediaSources = EmbyVersionAdapter.Instance.FindCompatibleMethod(
                        managerType,
                        "GetStaticMediaSources",
                        BindingFlags.Public | BindingFlags.Instance,
                        // 版本1 (4.9.1.80+): 5个参数 (item, enablePathSubstitution, enableUserData, profile, user)
                        new[] { typeof(BaseItem), typeof(bool), typeof(bool), typeof(DeviceProfile), typeof(User) },
                        // 版本2 (旧版本): 7个参数
                        new[] { typeof(BaseItem), typeof(bool), typeof(bool), typeof(bool), typeof(LibraryOptions), typeof(DeviceProfile), typeof(User) },
                        // 版本3 (可能的其他签名): 6个参数
                        new[] { typeof(BaseItem), typeof(bool), typeof(bool), typeof(bool), typeof(DeviceProfile), typeof(User) }
                    );
                    
                    if (_getStaticMediaSources != null)
                    {
                        _fallbackApproach = true;
                        var paramCount = _getStaticMediaSources.GetParameters().Length;
                        _logger.Info($"{nameof(MediaInfoApi)} - Found GetStaticMediaSources method ({paramCount} parameters) for Emby {currentVersion}");
                        
                        EmbyVersionAdapter.Instance.LogCompatibilityInfo(
                            nameof(MediaInfoApi), 
                            true, 
                            $"GetStaticMediaSources method located with {paramCount} parameters");
                    }
                }
                catch (Exception e)
                {
                    _logger.Error($"{nameof(MediaInfoApi)} - Failed to locate GetStaticMediaSources method: {e.Message}");
                    if (Plugin.Instance.DebugMode)
                    {
                        _logger.Debug(e.StackTrace);
                    }
                    
                    EmbyVersionAdapter.Instance.LogCompatibilityInfo(
                        nameof(MediaInfoApi), 
                        false, 
                        "GetStaticMediaSources method lookup failed");
                }

                if (_getStaticMediaSources is null)
                {
                    _logger.Info($"{nameof(MediaInfoApi)} - GetStaticMediaSources method not found via reflection - Will use public API");
                    // 如果找不到反射方法，但可以使用公共API，功能仍然可用
                    PatchTracker.FallbackPatchApproach = PatchApproach.Reflection;
                    _logger.Info($"{nameof(MediaInfoApi)} - Public API fallback enabled (feature works normally)");
                }
                else if (Plugin.Instance.IsModSupported)
                {
                    // 尝试ReversePatch，但即使失败也不会影响功能
                    var reversePatchSuccess = PatchManager.ReversePatch(PatchTracker, _getStaticMediaSources,
                        nameof(GetStaticMediaSourcesStub), suppressWarnings: false);
                    
                    if (!reversePatchSuccess && PatchTracker.FallbackPatchApproach == PatchApproach.Reflection)
                    {
                        // ReversePatch失败但可以使用Reflection，这是完全正常的
                        _logger.Info($"{nameof(MediaInfoApi)} - Using optimized Reflection (performance: excellent)");
                        EmbyVersionAdapter.Instance.LogCompatibilityInfo(
                            nameof(MediaInfoApi),
                            true,
                            "Reflection mode with direct method invoke - performance optimized");
                    }
                    else if (reversePatchSuccess)
                    {
                        _logger.Info($"{nameof(MediaInfoApi)} - Harmony ReversePatch active (performance: optimal)");
                        EmbyVersionAdapter.Instance.LogCompatibilityInfo(
                            nameof(MediaInfoApi),
                            true,
                            "Harmony ReversePatch - best performance");
                    }
                }
                else
                {
                    // 不支持Harmony，但找到了反射方法，使用Reflection
                    PatchTracker.FallbackPatchApproach = PatchApproach.Reflection;
                    _logger.Info($"{nameof(MediaInfoApi)} - Reflection approach active (Harmony not supported on this platform)");
                }
            }

            // 尝试修改LibraryMonitor以忽略.json文件（这个失败不影响MediaInfoApi的核心功能）
            try
            {
                var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
                var libraryMonitorImpl =
                    embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.IO.LibraryMonitor");
                var alwaysIgnoreExtensions = libraryMonitorImpl.GetField("_alwaysIgnoreExtensions",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var currentArray = (string[])alwaysIgnoreExtensions.GetValue(libraryMonitor);
                var newArray = new string[currentArray.Length + 1];
                Array.Copy(currentArray, newArray, currentArray.Length);
                newArray[newArray.Length - 1] = ".json";
                alwaysIgnoreExtensions.SetValue(libraryMonitor, newArray);
                
                if (Plugin.Instance.DebugMode)
                {
                    _logger.Debug("LibraryMonitor .json ignore extension added successfully");
                }
            }
            catch (Exception e)
            {
                // LibraryMonitor修改失败不影响MediaInfoApi的核心功能，只记录警告
                _logger.Warn($"{nameof(MediaInfoApi)} - Failed to modify LibraryMonitor (json file ignore). This is non-critical.");
                if (Plugin.Instance.DebugMode)
                {
                    _logger.Debug($"LibraryMonitor modification error: {e.Message}");
                    _logger.Debug(e.StackTrace);
                }
                // 不修改FallbackPatchApproach，保持之前设置的状态（Reflection或Harmony）
            }
        }

        [HarmonyReversePatch]
        private static List<MediaSourceInfo> GetStaticMediaSourcesStub(IMediaSourceManager instance, BaseItem item,
            bool enableAlternateMediaSources, bool enablePathSubstitution, bool fillChapters,
            LibraryOptions libraryOptions, DeviceProfile deviceProfile, User user = null) =>
            throw new NotImplementedException();

        private List<MediaSourceInfo> GetStaticMediaSourcesByApi(BaseItem item, bool enableAlternateMediaSources,
            LibraryOptions libraryOptions)
        {
            // Emby 4.9.1.80+: GetStaticMediaSources(BaseItem item, bool enablePathSubstitution, bool enableUserData, DeviceProfile profile, User user)
            // 参数顺序: item, enablePathSubstitution, enableUserData, profile, user
            return _mediaSourceManager.GetStaticMediaSources(item, enableAlternateMediaSources, false, null, null);
        }

        private List<MediaSourceInfo> GetStaticMediaSourcesByRef(BaseItem item, bool enableAlternateMediaSources,
            LibraryOptions libraryOptions)
        {
            using (PerformanceMonitor.Instance?.Measure("MediaInfoApi.GetStaticMediaSources"))
            {
                switch (PatchTracker.FallbackPatchApproach)
                {
                    case PatchApproach.Harmony:
                        try
                        {
                            return GetStaticMediaSourcesStub(_mediaSourceManager, item, enableAlternateMediaSources, false,
                                false, libraryOptions, null, null);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"Harmony stub invocation failed, falling back to reflection: {ex.Message}");
                            if (Plugin.Instance.DebugMode)
                            {
                                _logger.Debug(ex.StackTrace);
                            }
                            // 尝试降级到反射
                            PatchTracker.FallbackPatchApproach = PatchApproach.Reflection;
                            return GetStaticMediaSourcesByRef(item, enableAlternateMediaSources, libraryOptions);
                        }
                    
                case PatchApproach.Reflection:
                    if (_getStaticMediaSources == null)
                    {
                        // 反射方法不可用，使用公共API
                        return GetStaticMediaSourcesByApi(item, enableAlternateMediaSources, libraryOptions);
                    }
                    
                    try
                    {
                        // 动态检测参数数量并调用相应的方法签名
                        var parameters = _getStaticMediaSources.GetParameters();
                        object[] args;
                        
                        if (parameters.Length == 5)
                        {
                            // 版本1 (4.9.1.80+): (item, enablePathSubstitution, enableUserData, profile, user)
                            args = new object[] { item, enableAlternateMediaSources, false, null, null };
                        }
                        else if (parameters.Length == 7)
                        {
                            // 版本2 (旧版本): (item, enablePathSubstitution, enableUserData, fillChapters, libraryOptions, profile, user)
                            args = new object[] { item, enableAlternateMediaSources, false, false, libraryOptions, null, null };
                        }
                        else if (parameters.Length == 6)
                        {
                            // 版本3 (可能的其他签名): 6个参数
                            // 尝试智能匹配参数类型
                            if (parameters[4].ParameterType == typeof(LibraryOptions))
                            {
                                args = new object[] { item, enableAlternateMediaSources, false, false, libraryOptions, null };
                            }
                            else
                            {
                                args = new object[] { item, enableAlternateMediaSources, false, false, null, null };
                            }
                        }
                        else
                        {
                            _logger.Warn($"GetStaticMediaSources unexpected parameter count: {parameters.Length}");
                            _logger.Warn($"Parameter types: {string.Join(", ", parameters.Select(p => p.ParameterType.Name))}");
                            
                            // 尝试使用最常见的5参数版本
                            if (parameters.Length >= 5)
                            {
                                args = new object[] { item, enableAlternateMediaSources, false, null, null };
                                // 补充额外的null参数
                                if (parameters.Length > 5)
                                {
                                    var extendedArgs = new object[parameters.Length];
                                    Array.Copy(args, extendedArgs, Math.Min(args.Length, parameters.Length));
                                    args = extendedArgs;
                                }
                            }
                            else
                            {
                                // 参数太少，回退到公共API
                                _logger.Error($"Cannot invoke method with only {parameters.Length} parameters");
                                return GetStaticMediaSourcesByApi(item, enableAlternateMediaSources, libraryOptions);
                            }
                        }
                        
                        var result = _getStaticMediaSources.Invoke(_mediaSourceManager, args);
                        if (result is List<MediaSourceInfo> mediaSourceList)
                        {
                            return mediaSourceList;
                        }
                        else
                        {
                            _logger.Error($"Unexpected return type from GetStaticMediaSources: {result?.GetType().Name ?? "null"}");
                            return GetStaticMediaSourcesByApi(item, enableAlternateMediaSources, libraryOptions);
                        }
                    }
                    catch (TargetInvocationException tie)
                    {
                        var innerEx = tie.InnerException ?? tie;
                        _logger.Error($"Failed to invoke GetStaticMediaSources via reflection: {innerEx.Message}");
                        if (Plugin.Instance.DebugMode)
                        {
                            _logger.Debug($"Inner exception: {innerEx.GetType().Name}");
                            _logger.Debug(innerEx.StackTrace);
                        }
                        // 回退到公共API
                        return GetStaticMediaSourcesByApi(item, enableAlternateMediaSources, libraryOptions);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Failed to invoke GetStaticMediaSources via reflection: {ex.Message}");
                        if (Plugin.Instance.DebugMode)
                        {
                            _logger.Debug($"Exception type: {ex.GetType().Name}");
                            _logger.Debug(ex.StackTrace);
                        }
                        // 回退到公共API
                        return GetStaticMediaSourcesByApi(item, enableAlternateMediaSources, libraryOptions);
                    }
                    
                    default:
                        // 回退到公共API
                        return GetStaticMediaSourcesByApi(item, enableAlternateMediaSources, libraryOptions);
                }
            }
        }

        public List<MediaSourceInfo> GetStaticMediaSources(BaseItem item, bool enableAlternateMediaSources)
        {
            var options = _libraryManager.GetLibraryOptions(item);

            return !_fallbackApproach
                ? GetStaticMediaSourcesByApi(item, enableAlternateMediaSources, options)
                : GetStaticMediaSourcesByRef(item, enableAlternateMediaSources, options);
        }

        public MetadataRefreshOptions GetMediaInfoRefreshOptions()
        {
            return new MetadataRefreshOptions(new DirectoryService(_logger, _fileSystem))
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = false,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages = false,
                EnableThumbnailImageExtraction = false,
                EnableSubtitleDownloading = false
            };
        }

        public static string GetMediaInfoJsonPath(BaseItem item)
        {
            var jsonRootFolder = Plugin.Instance.MediaInfoExtractStore.GetOptions().MediaInfoJsonRootFolder;

            var relativePath = item.ContainingFolderPath;
            if (!string.IsNullOrEmpty(jsonRootFolder) && Path.IsPathRooted(item.ContainingFolderPath))
            {
                relativePath = Path.GetRelativePath(Path.GetPathRoot(item.ContainingFolderPath)!,
                    item.ContainingFolderPath);
            }

            var mediaInfoJsonPath = !string.IsNullOrEmpty(jsonRootFolder)
                ? Path.Combine(jsonRootFolder, relativePath, item.FileNameWithoutExtension + MediaInfoFileExtension)
                : Path.Combine(item.ContainingFolderPath!, item.FileNameWithoutExtension + MediaInfoFileExtension);

            return mediaInfoJsonPath;
        }

        private async Task<bool> SerializeMediaInfo(BaseItem item, IDirectoryService directoryService, bool overwrite,
            string source)
        {
            var mediaInfoJsonPath = GetMediaInfoJsonPath(item);
            var file = directoryService.GetFile(mediaInfoJsonPath);

            if (overwrite || file?.Exists != true)
            {
                try
                {
                    var options = _libraryManager.GetLibraryOptions(item);
                    var mediaSources = item.GetMediaSources(false, false, options);
                    var chapters = _itemRepository.GetChapters(item);
                    var mediaSourcesWithChapters = mediaSources.Select(mediaSource =>
                            new MediaSourceWithChapters
                                { MediaSourceInfo = mediaSource, Chapters = chapters })
                        .ToList();

                    foreach (var jsonItem in mediaSourcesWithChapters)
                    {
                        jsonItem.MediaSourceInfo.Id = null;
                        jsonItem.MediaSourceInfo.ItemId = null;
                        jsonItem.MediaSourceInfo.Path = null;

                        foreach (var subtitle in jsonItem.MediaSourceInfo.MediaStreams.Where(m =>
                                     m.IsExternal && m.Type == MediaStreamType.Subtitle &&
                                     m.Protocol == MediaProtocol.File))
                        {
                            subtitle.Path = _fileSystem.GetFileInfo(subtitle.Path).Name;
                        }

                        foreach (var chapter in jsonItem.Chapters)
                        {
                            chapter.ImageTag = null;
                        }

                        if (item is Episode)
                        {
                            jsonItem.ZeroFingerprintConfidence =
                                !string.IsNullOrEmpty(_itemRepository.GetIntroDetectionFailureResult(item.InternalId));
                        }

                        if (item is Audio)
                        {
                            var primaryImageInfo = item.GetImageInfo(ImageType.Primary, 0);
                            if (primaryImageInfo != null && _fileSystem.FileExists(primaryImageInfo.Path))
                            {
                                var imageBytes = await _fileSystem
                                    .ReadAllBytesAsync(primaryImageInfo.Path, CancellationToken.None)
                                    .ConfigureAwait(false);
                                var base64String = Convert.ToBase64String(imageBytes);
                                jsonItem.EmbeddedImage = base64String;
                            }
                        }
                    }

                    var parentDirectory = Path.GetDirectoryName(mediaInfoJsonPath);
                    if (!string.IsNullOrEmpty(parentDirectory))
                    {
                        Directory.CreateDirectory(parentDirectory);
                    }

                    _jsonSerializer.SerializeToFile(mediaSourcesWithChapters, mediaInfoJsonPath);

                    _logger.Info("MediaInfoPersist - Serialization Success (" + source + "): " + mediaInfoJsonPath);

                    return true;
                }
                catch (Exception e)
                {
                    _logger.Error("MediaInfoPersist - Serialization Failed (" + source + "): " + mediaInfoJsonPath);
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            return false;
        }

        public async Task<bool> SerializeMediaInfo(long itemId, IDirectoryService directoryService, bool overwrite,
            string source)
        {
            var workItem = _libraryManager.GetItemById(itemId);

            if (!Plugin.LibraryApi.IsLibraryInScope(workItem)) return false;

            if (!Plugin.LibraryApi.HasMediaInfo(workItem))
            {
                _logger.Info("MediaInfoPersist - Serialization Skipped - No MediaInfo (" + source + ")");
                return false;
            }
            
            var ds = directoryService ?? new DirectoryService(_logger, _fileSystem);

            return await SerializeMediaInfo(workItem, ds, overwrite, source).ConfigureAwait(false);
        }

        public async Task<bool> DeserializeMediaInfo(BaseItem item, IDirectoryService directoryService, string source,
            bool ignoreFileChange)
        {
            var workItem = _libraryManager.GetItemById(item.InternalId);

            if (Plugin.LibraryApi.HasMediaInfo(workItem)) return true;

            var mediaInfoJsonPath = GetMediaInfoJsonPath(item);
            var file = directoryService.GetFile(mediaInfoJsonPath);

            if (file?.Exists == true)
            {
                try
                {
                    var mediaSourceWithChapters =
                        (await _jsonSerializer
                            .DeserializeFromFileAsync<List<MediaSourceWithChapters>>(mediaInfoJsonPath)
                            .ConfigureAwait(false)).ToArray()[0];

                    if (mediaSourceWithChapters?.MediaSourceInfo?.RunTimeTicks.HasValue is true &&
                        (ignoreFileChange || !Plugin.LibraryApi.HasFileChanged(item, directoryService)))
                    {
                        foreach (var subtitle in mediaSourceWithChapters.MediaSourceInfo.MediaStreams.Where(m =>
                                     m.IsExternal && m.Type == MediaStreamType.Subtitle &&
                                     m.Protocol == MediaProtocol.File))
                        {
                            subtitle.Path = Path.Combine(workItem.ContainingFolderPath,
                                _fileSystem.GetFileInfo(subtitle.Path).Name);
                        }

                        _itemRepository.SaveMediaStreams(item.InternalId,
                            mediaSourceWithChapters.MediaSourceInfo.MediaStreams, CancellationToken.None);

                        if (workItem is Audio && !string.IsNullOrEmpty(mediaSourceWithChapters.EmbeddedImage))
                        {
                            var imageBytes = Convert.FromBase64String(mediaSourceWithChapters.EmbeddedImage);
                            var tempPath = Path.Combine(Plugin.Instance.ApplicationPaths.TempDirectory,
                                Guid.NewGuid() + ".jpg");
                            await _fileSystem.WriteAllBytesAsync(tempPath, imageBytes, CancellationToken.None)
                                .ConfigureAwait(false);

                            var libraryOptions = _libraryManager.GetLibraryOptions(workItem);
                            await _providerManager.SaveImage(workItem, libraryOptions, tempPath, ImageType.Primary,
                                    null, Array.Empty<long>(), directoryService, false, CancellationToken.None)
                                .ConfigureAwait(false);
                        }

                        workItem.Size = mediaSourceWithChapters.MediaSourceInfo.Size.GetValueOrDefault();
                        workItem.RunTimeTicks = mediaSourceWithChapters.MediaSourceInfo.RunTimeTicks;
                        workItem.Container = mediaSourceWithChapters.MediaSourceInfo.Container;
                        workItem.TotalBitrate = mediaSourceWithChapters.MediaSourceInfo.Bitrate.GetValueOrDefault();

                        var videoStream = mediaSourceWithChapters.MediaSourceInfo.MediaStreams
                            .Where(s => s.Type == MediaStreamType.Video && s.Width.HasValue && s.Height.HasValue)
                            .OrderByDescending(s => (long)s.Width.Value * s.Height.Value)
                            .FirstOrDefault();

                        if (videoStream != null)
                        {
                            workItem.Width = videoStream.Width.GetValueOrDefault();
                            workItem.Height = videoStream.Height.GetValueOrDefault();
                        }

                        _libraryManager.UpdateItems(new List<BaseItem> { workItem }, null,
                            ItemUpdateType.MetadataImport, false, false, null, CancellationToken.None);

                        if (workItem is Video video)
                        {
                            ChapterChangeTracker.BypassInstance(video);
                            await DeserializeChapterInfo(video, mediaSourceWithChapters.Chapters, directoryService,
                                source).ConfigureAwait(false);

                            if (video is Episode && mediaSourceWithChapters.ZeroFingerprintConfidence is true)
                            {
                                _itemRepository.LogIntroDetectionFailureFailure(video.InternalId,
                                    item.DateModified.ToUnixTimeSeconds());
                            }
                        }

                        _logger.Info("MediaInfoPersist - Deserialization Success (" + source + "): " + mediaInfoJsonPath);

                        return true;
                    }

                    _logger.Info("MediaInfoPersist - Deserialization Skipped (" + source + "): " + mediaInfoJsonPath);
                }
                catch (Exception e)
                {
                    _logger.Error("MediaInfoPersist - Deserialization Failed (" + source + "): " + mediaInfoJsonPath);
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            return false;
        }

        public void DeleteMediaInfoJson(BaseItem item, IDirectoryService directoryService, string source)
        {
            var mediaInfoJsonPath = GetMediaInfoJsonPath(item);

            var file = directoryService.GetFile(mediaInfoJsonPath);

            if (file?.Exists is true)
            {
                try
                {
                    _logger.Info($"MediaInfoPersist - Attempting to delete ({source}): {mediaInfoJsonPath}");
                    _fileSystem.DeleteFile(mediaInfoJsonPath);

                    var jsonRoot = Plugin.Instance.MediaInfoExtractStore.GetOptions().MediaInfoJsonRootFolder;

                    if (!string.IsNullOrWhiteSpace(jsonRoot))
                    {
                        jsonRoot = _fileSystem.GetFullPath(jsonRoot).TrimEnd(Path.DirectorySeparatorChar);

                        var currentDir =
                            _fileSystem.GetFullPath(_fileSystem.GetDirectoryName(mediaInfoJsonPath) ?? string.Empty);

                        while (!string.IsNullOrEmpty(currentDir) &&
                               !string.Equals(currentDir, jsonRoot, StringComparison.OrdinalIgnoreCase) &&
                               CommonUtility.IsDirectoryEmpty(currentDir))
                        {
                            _logger.Info(
                                $"MediaInfoPersist - Attempting to delete empty folder ({source}): {currentDir}");
                            _fileSystem.DeleteDirectory(currentDir, false);
                            currentDir = Path.GetDirectoryName(currentDir);
                        }
                    }
                }
                catch (Exception e)
                {
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }
        }

        public async Task<bool> DeserializeIntroMarker(Episode item, IDirectoryService directoryService, string source)
        {
            var mediaInfoJsonPath = GetMediaInfoJsonPath(item);
            var file = directoryService.GetFile(mediaInfoJsonPath);

            if (file?.Exists == true)
            {
                try
                {
                    var mediaSourceWithChapters =
                        (await _jsonSerializer
                            .DeserializeFromFileAsync<List<MediaSourceWithChapters>>(mediaInfoJsonPath)
                            .ConfigureAwait(false)).ToArray()[0];

                    if (mediaSourceWithChapters.ZeroFingerprintConfidence is true)
                    {
                        _itemRepository.LogIntroDetectionFailureFailure(item.InternalId,
                            item.DateModified.ToUnixTimeSeconds());

                        _logger.Info("ChapterInfoPersist - Log Zero Fingerprint Confidence (" + source + "): " + mediaInfoJsonPath);

                        return true;
                    }

                    var introStart = mediaSourceWithChapters.Chapters
                        .FirstOrDefault(c => c.MarkerType == MarkerType.IntroStart);

                    var introEnd = mediaSourceWithChapters.Chapters
                        .FirstOrDefault(c => c.MarkerType == MarkerType.IntroEnd);

                    if (introStart != null && introEnd != null && introEnd.StartPositionTicks > introStart.StartPositionTicks)
                    {
                        var chapters = _itemRepository.GetChapters(item);
                        chapters.RemoveAll(c =>
                            c.MarkerType == MarkerType.IntroStart || c.MarkerType == MarkerType.IntroEnd);
                        chapters.Add(introStart);
                        chapters.Add(introEnd);
                        chapters.Sort((c1, c2) => c1.StartPositionTicks.CompareTo(c2.StartPositionTicks));

                        ChapterChangeTracker.BypassInstance(item);
                        _itemRepository.SaveChapters(item.InternalId, chapters);

                        _logger.Info("ChapterInfoPersist - Deserialization Success (" + source + "): " + mediaInfoJsonPath);

                        return true;
                    }
                }
                catch (Exception e)
                {
                    _logger.Error("ChapterInfoPersist - Deserialization Failed (" + source + "): " + mediaInfoJsonPath);
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            return false;
        }

        public async Task<bool> DeserializeChapterInfo(Video item, List<ChapterInfo> chapters,
            IDirectoryService directoryService, string source)
        {
            var thumbnailResult = false;

            if (Plugin.Instance.IsModSupported || !item.IsShortcut)
            {
                var localThumbnailSets =
                    Video.GetLocalThumbnailSetInfos(item.Path, item.Id, false, directoryService);

                if (localThumbnailSets.Length > 0)
                {
                    var options = _libraryManager.GetLibraryOptions(item);
                    var dummyLibraryOptions = new LibraryOptions
                    {
                        ThumbnailImagesIntervalSeconds = 10,
                        CacheImages = options.CacheImages
                    };

                    thumbnailResult = await Plugin.VideoThumbnailApi.RefreshThumbnailImages(item,
                        dummyLibraryOptions, directoryService, chapters, false, false,
                        CancellationToken.None);

                    if (thumbnailResult)
                    {
                        _logger.Info("ChapterInfoPersist - Video Thumbnail Restore Success (" + source + "): " +
                                     localThumbnailSets[0].Path);
                    }
                }
            }

            _itemRepository.SaveChapters(item.InternalId, true, chapters);

            return thumbnailResult;
        }

        public void QueueRefreshAlternateVersions(BaseItem item, MetadataRefreshOptions options, bool force)
        {
            if (!(item is Video video)) return;

            var altIds = video.GetAlternateVersionIds();

            if (!altIds.Any()) return;

            var itemsToRefresh = force
                ? altIds
                : _libraryManager.GetItemList(new InternalItemsQuery
                    {
                        ItemIds = altIds.ToArray(),
                        HasPath = true,
                        HasAudioStream = false,
                        MediaTypes = new[] { MediaType.Video }
                    })
                    .Select(i => i.InternalId);

            foreach (var altId in itemsToRefresh)
            {
                _providerManager.QueueRefresh(altId, options, RefreshPriority.Normal);
            }
        }

        public BaseItem GetItemByMediaSourceId(BaseItem item, string mediaSourceId)
        {
            if (string.IsNullOrEmpty(mediaSourceId)) return null;

            BaseItem targetItem = null;

            if (item.GetDefaultMediaSourceId() == mediaSourceId)
            {
                targetItem = item;
            }
            else
            {
                var mediaSource = item.GetMediaSources(true, false, null).FirstOrDefault(s => s.Id == mediaSourceId);

                if (mediaSource != null)
                {
                    targetItem = _libraryManager.GetItemById(mediaSource.ItemId);
                }
            }

            return targetItem;
        }
    }
}
