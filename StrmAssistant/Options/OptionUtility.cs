using Emby.Media.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Playlists;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using static StrmAssistant.Options.GeneralOptions;
using static StrmAssistant.Options.IntroSkipOptions;
using static StrmAssistant.Options.MediaInfoExtractOptions;
using static StrmAssistant.Options.ModOptions;

namespace StrmAssistant.Options
{
    public static class Utility
    {
        private static HashSet<string> _selectedExclusiveFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<long, ConcurrentDictionary<string, byte>> ItemExclusiveFeatures =
            new ConcurrentDictionary<long, ConcurrentDictionary<string, byte>>();

        private static HashSet<string> _selectedCatchupTasks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> _selectedIntroSkipPreferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string[] _includeItemTypes = Array.Empty<string>();

        public static void UpdateExclusiveControlFeatures(string currentScope)
        {
            _selectedExclusiveFeatures = new HashSet<string>(
                currentScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(f => !(f == ExclusiveControl.CatchAllAllow.ToString() &&
                                  currentScope.Contains(ExclusiveControl.CatchAllBlock.ToString()))) ??
                Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsExclusiveFeatureSelected(long? itemId = null, params ExclusiveControl[] featuresToCheck)
        {
            if (itemId.HasValue && ItemExclusiveFeatures.TryGetValue(itemId.Value, out var itemFeatures))
            {
                return featuresToCheck.Any(f => itemFeatures.ContainsKey(f.ToString()));
            }

            return featuresToCheck.Any(f => _selectedExclusiveFeatures.Contains(f.ToString()));
        }

        public static bool IsExclusiveFeatureSelected(params ExclusiveControl[] featuresToCheck)
        {
            return IsExclusiveFeatureSelected(null, featuresToCheck);
        }

        public static void EnableItemExclusiveFeatures(long itemId, params ExclusiveControl[] features)
        {
            var itemFeatures = ItemExclusiveFeatures.GetOrAdd(itemId, _ => new ConcurrentDictionary<string, byte>());

            foreach (var feature in features)
            {
                itemFeatures.TryAdd(feature.ToString(), 0);
            }
        }

        public static void DisableItemExclusiveFeatures(long itemId, params ExclusiveControl[] features)
        {
            if (ItemExclusiveFeatures.TryGetValue(itemId, out var itemFeatures))
            {
                foreach (var feature in features)
                {
                    itemFeatures.TryRemove(feature.ToString(), out _);
                }

                if (itemFeatures.IsEmpty)
                {
                    ItemExclusiveFeatures.TryRemove(itemId, out _);
                }
            }
        }

        public static void ClearItemExclusiveFeatures(long itemId)
        {
            ItemExclusiveFeatures.TryRemove(itemId, out _);
        }

        public static string GetSelectedExclusiveFeatureDescription()
        {
            return string.Join(", ",
                _selectedExclusiveFeatures
                    .Select(feature =>
                        Enum.TryParse(feature.Trim(), true, out ExclusiveControl type)
                            ? type
                            : (ExclusiveControl?)null)
                    .Where(type => type.HasValue)
                    .OrderBy(type => type)
                    .Select(type => type.Value.GetDescription()));
        }

        public static void UpdateCatchupScope(string currentScope)
        {
            _selectedCatchupTasks = new HashSet<string>(
                currentScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) ??
                Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsCatchupTaskSelected(params CatchupTask[] tasksToCheck)
        {
            return tasksToCheck.Any(f => _selectedCatchupTasks.Contains(f.ToString()));
        }

        public static string GetSelectedCatchupTaskDescription()
        {
            return string.Join(", ",
                _selectedCatchupTasks
                    .Select(task =>
                        Enum.TryParse(task.Trim(), true, out CatchupTask type)
                            ? type
                            : (CatchupTask?)null)
                    .Where(type => type.HasValue)
                    .OrderBy(type => type)
                    .Select(type => type.Value.GetDescription()));
        }

        public static void UpdateIntroSkipPreferences(string currentPreferences)
        {
            _selectedIntroSkipPreferences = new HashSet<string>(
                currentPreferences?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) ??
                Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public static bool IsIntroSkipPreferenceSelected(params IntroSkipPreference[] preferencesToCheck)
        {
            return preferencesToCheck.Any(f => _selectedIntroSkipPreferences.Contains(f.ToString()));
        }

        public static string GetSelectedIntroSkipPreferenceDescription()
        {
            return string.Join(", ",
                _selectedIntroSkipPreferences
                    .Select(pref =>
                        Enum.TryParse(pref.Trim(), true, out IntroSkipPreference type)
                            ? type
                            : (IntroSkipPreference?)null)
                    .Where(type => type.HasValue)
                    .OrderBy(type => type)
                    .Select(type => type.Value.GetDescription()));
        }

        public static void UpdateSearchScope(string currentScope)
        {
            var searchScope = currentScope?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) ??
                              Array.Empty<string>();

            var includeItemTypes = new List<string>();

            foreach (var scope in searchScope)
            {
                if (Enum.TryParse(scope, true, out SearchItemType type))
                {
                    switch (type)
                    {
                        case SearchItemType.Book:
                            includeItemTypes.AddRange(new[] { nameof(Book) });
                            break;
                        case SearchItemType.Collection:
                            includeItemTypes.AddRange(new[] { nameof(BoxSet) });
                            break;
                        case SearchItemType.Episode:
                            includeItemTypes.AddRange(new[] { nameof(Episode) });
                            break;
                        case SearchItemType.Game:
                            includeItemTypes.AddRange(new[] { nameof(Game), nameof(GameSystem) });
                            break;
                        case SearchItemType.Genre:
                            includeItemTypes.AddRange(new[] { nameof(MusicGenre), nameof(GameGenre), nameof(Genre) });
                            break;
                        case SearchItemType.LiveTv:
                            includeItemTypes.AddRange(new[]
                            {
                                nameof(LiveTvChannel), nameof(LiveTvProgram), "LiveTVSeries"
                            });
                            break;
                        case SearchItemType.Movie:
                            includeItemTypes.AddRange(new[] { nameof(Movie) });
                            break;
                        case SearchItemType.Music:
                            includeItemTypes.AddRange(new[] { nameof(Audio), nameof(MusicVideo) });
                            break;
                        case SearchItemType.MusicAlbum:
                            includeItemTypes.AddRange(new[] { nameof(MusicAlbum) });
                            break;
                        case SearchItemType.Person:
                            includeItemTypes.AddRange(new[] { nameof(Person) });
                            break;
                        case SearchItemType.MusicArtist:
                            includeItemTypes.AddRange(new[] { nameof(MusicArtist) });
                            break;
                        case SearchItemType.Photo:
                            includeItemTypes.AddRange(new[] { nameof(Photo) });
                            break;
                        case SearchItemType.PhotoAlbum:
                            includeItemTypes.AddRange(new[] { nameof(PhotoAlbum) });
                            break;
                        case SearchItemType.Playlist:
                            includeItemTypes.AddRange(new[] { nameof(Playlist) });
                            break;
                        case SearchItemType.Series:
                            includeItemTypes.AddRange(new[] { nameof(Series) });
                            break;
                        case SearchItemType.Season:
                            includeItemTypes.AddRange(new[] { nameof(Season) });
                            break;
                        case SearchItemType.Studio:
                            includeItemTypes.AddRange(new[] { nameof(Studio) });
                            break;
                        case SearchItemType.Tag:
                            includeItemTypes.AddRange(new[] { nameof(Tag) });
                            break;
                        case SearchItemType.Trailer:
                            includeItemTypes.AddRange(new[] { nameof(Trailer) });
                            break;
                        case SearchItemType.Video:
                            includeItemTypes.AddRange(new[] { nameof(Video) });
                            break;
                    }
                }
            }

            _includeItemTypes = includeItemTypes.ToArray();
        }

        public static string[] GetSearchScope()
        {
            return _includeItemTypes;
        }
    }
}
