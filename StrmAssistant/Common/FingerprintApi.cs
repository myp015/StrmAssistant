using HarmonyLib;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Common;
using StrmAssistant.Mod;
using StrmAssistant.Options;
using StrmAssistant.Properties;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.Common
{
    public class FingerprintApi
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;
        private readonly IItemRepository _itemRepository;
        private readonly ILogger _logger;

        private static readonly PatchTracker PatchTracker =
            new PatchTracker(typeof(FingerprintApi),
                Plugin.Instance.IsModSupported ? PatchApproach.Harmony : PatchApproach.Reflection);
        private readonly object _audioFingerprintManager;
        private readonly MethodInfo _createTitleFingerprint;
        private readonly MethodInfo _getAllFingerprintFilesForSeason;
        private readonly MethodInfo _updateSequencesForSeason;
        private readonly FieldInfo _timeoutMs;

        public static List<string> LibraryPathsInScope;

        public FingerprintApi(ILibraryManager libraryManager, IFileSystem fileSystem,
            IApplicationPaths applicationPaths, IFfmpegManager ffmpegManager, IMediaEncoder mediaEncoder,
            IMediaMountManager mediaMountManager, IJsonSerializer jsonSerializer, IItemRepository itemRepository,
            IServerApplicationHost serverApplicationHost)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
            _itemRepository = itemRepository;

            UpdateLibraryPathsInScope();

            try
            {
                var embyProviders = EmbyVersionCompatibility.TryLoadAssembly("Emby.Providers");
                if (embyProviders == null)
                {
                    _logger.Error($"{nameof(FingerprintApi)} - Failed to load Emby.Providers assembly");
                    PatchTracker.FallbackPatchApproach = PatchApproach.None;
                    return;
                }

                var audioFingerprintManager = EmbyVersionCompatibility.TryGetType(embyProviders, "Emby.Providers.Markers.AudioFingerprintManager");
                if (audioFingerprintManager != null)
                {
                    var audioFingerprintManagerConstructor = audioFingerprintManager.GetConstructor(
                        BindingFlags.Public | BindingFlags.Instance, null,
                        new[]
                        {
                            typeof(IFileSystem), typeof(ILogger), typeof(IApplicationPaths), typeof(IFfmpegManager),
                            typeof(IMediaEncoder), typeof(IMediaMountManager), typeof(IJsonSerializer),
                            typeof(IServerApplicationHost)
                        }, null);
                    
                    if (audioFingerprintManagerConstructor != null)
                    {
                        _audioFingerprintManager = audioFingerprintManagerConstructor.Invoke(new object[]
                        {
                            fileSystem, _logger, applicationPaths, ffmpegManager, mediaEncoder, mediaMountManager,
                            jsonSerializer, serverApplicationHost
                        });
                        
                        _createTitleFingerprint = EmbyVersionCompatibility.FindCompatibleMethod(
                            audioFingerprintManager,
                            "CreateTitleFingerprint",
                            BindingFlags.Public | BindingFlags.Instance,
                            new[] { typeof(Episode), typeof(LibraryOptions), typeof(IDirectoryService), typeof(CancellationToken) },
                            new[] { typeof(Episode), typeof(IDirectoryService), typeof(CancellationToken) },
                            new[] { typeof(Episode), typeof(CancellationToken) }
                        );
                        
                        _getAllFingerprintFilesForSeason = audioFingerprintManager.GetMethod("GetAllFingerprintFilesForSeason",
                            BindingFlags.Public | BindingFlags.Instance);
                        
                        var updateMethods = audioFingerprintManager.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                            .Where(m => m.Name == "UpdateSequencesForSeason")
                            .ToArray();
                        
                        if (updateMethods.Length > 0)
                        {
                            _updateSequencesForSeason = updateMethods.OrderByDescending(m => m.GetParameters().Length).First();
                            
                            if (Plugin.Instance.DebugMode)
                            {
                                var paramCount = _updateSequencesForSeason.GetParameters().Length;
                                _logger.Debug($"{nameof(FingerprintApi)}: Found UpdateSequencesForSeason with {paramCount} parameters");
                            }
                        }
                        
                        _timeoutMs = audioFingerprintManager.GetField("TimeoutMs",
                            BindingFlags.NonPublic | BindingFlags.Instance);

                        if (_audioFingerprintManager != null && _timeoutMs != null)
                        {
                            PatchTimeout(Plugin.Instance.MainOptionsStore.GetOptions().GeneralOptions.MaxConcurrentCount);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                _logger.Error($"{nameof(FingerprintApi)} - Initialization failed: {e.Message}");
            }

            if (_audioFingerprintManager is null || _createTitleFingerprint is null ||
                _getAllFingerprintFilesForSeason is null || _updateSequencesForSeason is null || _timeoutMs is null)
            {
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
            }
            else if (Plugin.Instance.IsModSupported)
            {
                PatchManager.ReversePatch(PatchTracker, _createTitleFingerprint, nameof(CreateTitleFingerprintStub));
                PatchManager.ReversePatch(PatchTracker, _getAllFingerprintFilesForSeason, nameof(GetAllFingerprintFilesForSeasonStub));
                PatchManager.ReversePatch(PatchTracker, _updateSequencesForSeason, nameof(UpdateSequencesForSeasonStub));
            }
        }

#pragma warning disable CS1998
        [HarmonyReversePatch]
        private static async Task<Tuple<string, bool>> CreateTitleFingerprintStub(object instance, Episode item,
            LibraryOptions libraryOptions, IDirectoryService directoryService, CancellationToken cancellationToken) =>
            throw new NotImplementedException();
#pragma warning restore CS1998

        public Task<Tuple<string, bool>> CreateTitleFingerprint(Episode item, IDirectoryService directoryService,
            CancellationToken cancellationToken)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            switch (PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    try { return CreateTitleFingerprintStub(_audioFingerprintManager, item, libraryOptions, directoryService, cancellationToken); }
                    catch { PatchTracker.FallbackPatchApproach = PatchApproach.Reflection; return CreateTitleFingerprint(item, directoryService, cancellationToken); }
                case PatchApproach.Reflection:
                    try { return _createTitleFingerprint.Invoke(_audioFingerprintManager, new object[] { item, libraryOptions, directoryService, cancellationToken }) as Task<Tuple<string, bool>> ?? Task.FromResult<Tuple<string, bool>>(null); }
                    catch { return Task.FromResult<Tuple<string, bool>>(null); }
                default:
                    return Task.FromResult<Tuple<string, bool>>(null);
            }
        }

        public Task<Tuple<string, bool>> CreateTitleFingerprint(Episode item, CancellationToken cancellationToken)
        {
            return CreateTitleFingerprint(item, new DirectoryService(_logger, _fileSystem), cancellationToken);
        }

#pragma warning disable CS1998
        [HarmonyReversePatch]
        private static async Task<object> GetAllFingerprintFilesForSeasonStub(object instance, Season season,
            Episode[] episodes, LibraryOptions libraryOptions, IDirectoryService directoryService,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();
#pragma warning restore CS1998

        private Task<object> GetAllFingerprintFilesForSeason(Season season, Episode[] episodes,
            LibraryOptions libraryOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            switch (PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    return GetAllFingerprintFilesForSeasonStub(_audioFingerprintManager, season, episodes, libraryOptions, directoryService, cancellationToken);
                case PatchApproach.Reflection:
                    return (Task<object>)_getAllFingerprintFilesForSeason.Invoke(_audioFingerprintManager, new object[] { season, episodes, libraryOptions, directoryService, cancellationToken });
                default:
                    throw new NotImplementedException();
            }
        }

        [HarmonyReversePatch]
        private static void UpdateSequencesForSeasonStub(object instance, Season season, object seasonFingerprintInfo,
            Episode episode, LibraryOptions libraryOptions, IDirectoryService directoryService, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        private void UpdateSequencesForSeason(Season season, object seasonFingerprintInfo, Episode episode,
            LibraryOptions libraryOptions, IDirectoryService directoryService, CancellationToken cancellationToken)
        {
            switch (PatchTracker.FallbackPatchApproach)
            {
                case PatchApproach.Harmony:
                    UpdateSequencesForSeasonStub(_audioFingerprintManager, season, seasonFingerprintInfo, episode, libraryOptions, directoryService, cancellationToken);
                    break;
                case PatchApproach.Reflection:
                    var args = _updateSequencesForSeason.GetParameters().Length == 6 
                        ? new[] { season, seasonFingerprintInfo, episode, libraryOptions, directoryService, cancellationToken }
                        : new[] { season, seasonFingerprintInfo, episode, libraryOptions, directoryService };
                    _updateSequencesForSeason.Invoke(_audioFingerprintManager, args);
                    break;
                default:
                    throw new NotImplementedException();
            }
        }

        public void PatchTimeout(int maxConcurrentCount)
        {
            var newTimeout = maxConcurrentCount * Convert.ToInt32(TimeSpan.FromMinutes(10.0).TotalMilliseconds);
            _timeoutMs.SetValue(_audioFingerprintManager, newTimeout);
        }

        public bool IsLibraryInScope(BaseItem item)
        {
            return !string.IsNullOrEmpty(item.Path) && LibraryPathsInScope.Any(l => item.Path.StartsWith(l));
        }

        public void UpdateLibraryPathsInScope(string currentScope)
        {
            var validLibraryIds = GetValidLibraryIds(currentScope);
            var libraries = _libraryManager.GetVirtualFolders()
                .Where(f => f.LibraryOptions.EnableMarkerDetection &&
                            (f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null) &&
                            (!validLibraryIds.Any() || validLibraryIds.All(id => id == "-1") ||
                             validLibraryIds.Contains(f.Id)))
                .ToList();

            LibraryPathsInScope = libraries.SelectMany(l => l.Locations)
                .Select(ls => ls.EndsWith(Path.DirectorySeparatorChar.ToString()) ? ls : ls + Path.DirectorySeparatorChar)
                .ToList();
        }

        public void UpdateLibraryPathsInScope()
        {
            UpdateLibraryPathsInScope(Plugin.Instance.IntroSkipStore.GetOptions().MarkerEnabledLibraryScope);
        }

        public HashSet<long> GetAllBlacklistSeasons()
        {
            var blacklistShowIds = Plugin.Instance.IntroSkipStore.GetOptions()
                .FingerprintBlacklistShows.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => long.TryParse(part.Trim(), out var id) ? id : (long?)null)
                .Where(id => id.HasValue).Select(id => id.Value).ToArray();

            if (!blacklistShowIds.Any()) return new HashSet<long>();

            var items = _libraryManager.GetItemList(new InternalItemsQuery { ItemIds = blacklistShowIds });
            var seasons = items.OfType<Season>().Select(s => s.InternalId).ToList();
            seasons.AddRange(items.OfType<Series>().SelectMany(series => _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Season) },
                ParentWithPresentationUniqueKeyFromItemId = series.InternalId
            }).OfType<Season>().Select(s => s.InternalId)));

            return new HashSet<long>(seasons);
        }

        public long[] GetAllFavoriteSeasons()
        {
            var favorites = LibraryApi.AllUsers.Select(e => e.Key)
                .SelectMany(u => _libraryManager.GetItemList(new InternalItemsQuery
                {
                    User = u, IsFavorite = true, IncludeItemTypes = new[] { nameof(Series), nameof(Episode) },
                    PathStartsWithAny = LibraryPathsInScope.ToArray()
                })).GroupBy(i => i.InternalId).Select(g => g.First()).ToList();

            var expanded = Plugin.LibraryApi.ExpandFavorites(favorites, false, null, false).OfType<Episode>();
            var result = expanded.GroupBy(e => e.ParentId).Select(g => g.Key).ToArray();
            var blackListSeasons = GetAllBlacklistSeasons();
            return result.Where(s => !blackListSeasons.Contains(s)).ToArray();
        }

        public List<Episode> FetchFingerprintQueueItems(List<BaseItem> items)
        {
            var libraryIds = Plugin.Instance.IntroSkipStore.GetOptions().MarkerEnabledLibraryScope?
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToArray();

            var includeFavorites = libraryIds?.Contains("-1") == true;
            var resultItems = new List<Episode>();
            var incomingItems = items.OfType<Episode>().ToList();

            if (IsCatchupTaskSelected(GeneralOptions.CatchupTask.Fingerprint) && LibraryPathsInScope.Any())
            {
                if (includeFavorites) resultItems = Plugin.LibraryApi.ExpandFavorites(items, true, null, false).OfType<Episode>().ToList();
                if (libraryIds is null || !libraryIds.Any() || libraryIds.Any(id => id != "-1"))
                {
                    var filteredItems = incomingItems.Where(i => LibraryPathsInScope.Any(p => i.ContainingFolderPath.StartsWith(p))).ToList();
                    resultItems = resultItems.Concat(filteredItems).ToList();
                }
            }

            var isModSupported = Plugin.Instance.IsModSupported;
            resultItems = resultItems.Where(i => isModSupported || !i.IsShortcut).GroupBy(i => i.InternalId).Select(g => g.First()).ToList();
            var blackListSeasons = GetAllBlacklistSeasons();
            return FilterUnprocessed(resultItems.Where(e => !blackListSeasons.Contains(e.ParentId)).ToList());
        }

        private List<Episode> FilterUnprocessed(List<Episode> items)
        {
            var enableImageCapture = Plugin.Instance.MediaInfoExtractStore.GetOptions().EnableImageCapture;
            var results = items.Where(item => Plugin.LibraryApi.IsExtractNeeded(item, enableImageCapture) || IsExtractNeeded(item)).ToList();
            _logger.Info("IntroFingerprintExtract - Number of items: " + results.Count);
            return results;
        }

        public bool IsExtractNeeded(BaseItem item)
        {
            return !Plugin.ChapterApi.HasIntro(item) && string.IsNullOrEmpty(_itemRepository.GetIntroDetectionFailureResult(item.InternalId));
        }

        public List<Episode> FetchIntroPreExtractTaskItems()
        {
            var markerEnabledLibraryScope = Plugin.Instance.IntroSkipStore.GetOptions().MarkerEnabledLibraryScope;
            var itemsFingerprintQuery = new InternalItemsQuery { IncludeItemTypes = new[] { nameof(Episode) }, Recursive = true, GroupByPresentationUniqueKey = false, HasPath = true, HasAudioStream = false };

            if (!string.IsNullOrEmpty(markerEnabledLibraryScope) && markerEnabledLibraryScope.Contains("-1"))
                itemsFingerprintQuery.ParentIds = GetAllFavoriteSeasons().DefaultIfEmpty(-1).ToArray();
            else
            {
                if (LibraryPathsInScope.Any()) itemsFingerprintQuery.PathStartsWithAny = LibraryPathsInScope.ToArray();
                var blackListSeasons = GetAllBlacklistSeasons();
                if (blackListSeasons.Any()) itemsFingerprintQuery.ExcludeParentIds = blackListSeasons.ToArray();
            }

            return _libraryManager.GetItemList(itemsFingerprintQuery).Where(i => Plugin.Instance.IsModSupported || !i.IsShortcut).OfType<Episode>().ToList();
        }

        public List<Episode> FetchIntroFingerprintTaskItems()
        {
            var libraryIds = Plugin.Instance.IntroSkipStore.GetOptions().MarkerEnabledLibraryScope.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToArray();
            var introDetectionFingerprintMinutes = Plugin.Instance.IntroSkipStore.GetOptions().IntroDetectionFingerprintMinutes;

            var itemsFingerprintQuery = new InternalItemsQuery { 
                IncludeItemTypes = new[] { nameof(Episode) }, Recursive = true, GroupByPresentationUniqueKey = false, 
                WithoutChapterMarkers = new[] { MarkerType.IntroStart }, MinRunTimeTicks = TimeSpan.FromMinutes(introDetectionFingerprintMinutes).Ticks, 
                HasIntroDetectionFailure = false, HasAudioStream = true 
            };

            if (libraryIds.All(i => i == "-1")) itemsFingerprintQuery.ParentIds = GetAllFavoriteSeasons().DefaultIfEmpty(-1).ToArray();
            else
            {
                if (LibraryPathsInScope.Any()) itemsFingerprintQuery.PathStartsWithAny = LibraryPathsInScope.ToArray();
                var blackListSeasons = GetAllBlacklistSeasons();
                if (blackListSeasons.Any()) itemsFingerprintQuery.ExcludeParentIds = blackListSeasons.ToArray();
            }

            return _libraryManager.GetItemList(itemsFingerprintQuery).Where(i => Plugin.Instance.IsModSupported || !i.IsShortcut).OfType<Episode>().ToList();
        }

        public void UpdateLibraryIntroDetectionFingerprintLength(int currentLength)
        {
            var libraries = _libraryManager.GetVirtualFolders().Where(f => f.CollectionType == CollectionType.TvShows.ToString() || f.CollectionType is null).ToList();
            foreach (var library in libraries)
            {
                var options = library.LibraryOptions;
                if (options.IntroDetectionFingerprintLength != currentLength && long.TryParse(library.ItemId, out var itemId))
                {
                    options.IntroDetectionFingerprintLength = currentLength;
                    CollectionFolder.SaveLibraryOptions(itemId, options);
                }
            }
        }

        public void UpdateLibraryIntroDetectionFingerprintLength()
        {
            UpdateLibraryIntroDetectionFingerprintLength(Plugin.Instance.IntroSkipStore.GetOptions().IntroDetectionFingerprintMinutes);
        }

#nullable enable
        public async Task UpdateIntroMarkerForSeason(Season season, CancellationToken cancellationToken, IProgress<double>? progress = null)
        {
            var minutes = Plugin.Instance.IntroSkipStore.GetOptions().IntroDetectionFingerprintMinutes;
            var libraryOptions = _libraryManager.GetLibraryOptions(season);
            var directoryService = new DirectoryService(_logger, _fileSystem);

            var query = new InternalItemsQuery { GroupByPresentationUniqueKey = false, EnableTotalRecordCount = false, MinRunTimeTicks = TimeSpan.FromMinutes(minutes).Ticks, HasIntroDetectionFailure = false, HasAudioStream = true };
            var allEpisodes = season.GetEpisodes(query).Items.OfType<Episode>().ToArray();

            query.WithoutChapterMarkers = new[] { MarkerType.IntroStart };
            var targetEpisodes = season.GetEpisodes(query).Items.OfType<Episode>().ToList();

            var seasonFingerprintInfo = await GetAllFingerprintFilesForSeason(season, allEpisodes, libraryOptions, directoryService, cancellationToken).ConfigureAwait(false);

            for (int i = 0; i < targetEpisodes.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                UpdateSequencesForSeason(season, seasonFingerprintInfo, targetEpisodes[i], libraryOptions, directoryService, cancellationToken);
                progress?.Report((double)(i + 1) / targetEpisodes.Count);
            }
            progress?.Report(1.0);
        }
#nullable restore
    }
}