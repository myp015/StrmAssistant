using HarmonyLib;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Common;
using StrmAssistant.ScheduledTask;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Common.LanguageUtility;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class ChineseMovieDb : PatchBase<ChineseMovieDb>
    {
        private static Assembly _movieDbAssembly;
        private static ConstructorInfo _genericMovieDbInfoConstructor;
        private static MethodInfo _genericMovieDbInfoProcessMainInfoMovie;
        private static MethodInfo _genericMovieDbInfoIsCompleteMovie;
        private static MethodInfo _getTitleMovieData;

        private static MethodInfo _getMovieDbMetadataLanguages;
        private static MethodInfo _mapLanguageToProviderLanguage;
        private static MethodInfo _getImageLanguagesParam;
        private static FieldInfo _cacheTime;
        private static MethodInfo _getEpisodeInfoAsync;

        private static MethodInfo _movieDbSeriesProviderIsComplete;
        private static MethodInfo _movieDbSeriesProviderImportData;
        private static MethodInfo _ensureSeriesInfo;
        private static MethodInfo _getTitleSeriesInfo;
        private static PropertyInfo _nameSeriesInfoProperty;
        private static PropertyInfo _alternativeTitleSeriesInfoProperty;
        private static PropertyInfo _alternativeTitleListProperty;
        private static PropertyInfo _alternativeTitle;
        private static PropertyInfo _alternativeTitleCountryCode;
        private static PropertyInfo _genresProperty;
        private static PropertyInfo _genreNameProperty;

        private static MethodInfo _movieDbSeasonProviderIsComplete;
        private static MethodInfo _movieDbSeasonProviderImportData;
        private static PropertyInfo _nameSeasonInfoProperty;
        private static PropertyInfo _overviewSeasonInfoProperty;

        private static MethodInfo _movieDbEpisodeProviderIsComplete;
        private static MethodInfo _movieDbEpisodeProviderImportData;
        private static PropertyInfo _nameEpisodeInfoProperty;
        private static PropertyInfo _overviewEpisodeInfoProperty;

        private static PropertyInfo _seriesInfoTaskResultProperty;

        private static readonly AsyncLocal<string> CurrentLookupLanguageCountryCode = new AsyncLocal<string>();

        public ChineseMovieDb()
        {
            Initialize();

            PatchCacheTime();

            if (Plugin.Instance.MetadataEnhanceStore.GetOptions().ChineseMovieDb)
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
                var genericMovieDbInfo = _movieDbAssembly.GetType("MovieDb.GenericMovieDbInfo`1");
                var genericMovieDbInfoMovie = genericMovieDbInfo.MakeGenericType(typeof(Movie));
                _genericMovieDbInfoConstructor = genericMovieDbInfoMovie.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public, null,
                    new[]
                    {
                        typeof(IJsonSerializer), typeof(IHttpClient), typeof(IFileSystem),
                        typeof(IServerConfigurationManager), typeof(ILogManager), typeof(ILocalizationManager),
                        typeof(IServerApplicationHost), typeof(ILibraryManager)
                    }, null);
                _genericMovieDbInfoIsCompleteMovie = genericMovieDbInfoMovie.GetMethod("IsComplete",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _genericMovieDbInfoProcessMainInfoMovie = genericMovieDbInfoMovie.GetMethod("ProcessMainInfo",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var completeMovieData = _movieDbAssembly.GetType("MovieDb.MovieDbProvider")
                    .GetNestedType("CompleteMovieData", BindingFlags.NonPublic);
                _getTitleMovieData = completeMovieData.GetMethod("GetTitle");
                var movieDbProviderBase = _movieDbAssembly.GetType("MovieDb.MovieDbProviderBase");
                _getMovieDbMetadataLanguages = movieDbProviderBase.GetMethod("GetMovieDbMetadataLanguages",
                    BindingFlags.Public | BindingFlags.Instance);
                _mapLanguageToProviderLanguage = movieDbProviderBase.GetMethod("MapLanguageToProviderLanguage",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                ReversePatch(PatchTracker, _mapLanguageToProviderLanguage, nameof(MapLanguageToProviderLanguageStub));
                _getImageLanguagesParam = movieDbProviderBase.GetMethod("GetImageLanguagesParam",
                    BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string[]) }, null);
                _cacheTime = movieDbProviderBase.GetField("CacheTime", BindingFlags.Public | BindingFlags.Static);

                var movieDbSeriesProvider = _movieDbAssembly.GetType("MovieDb.MovieDbSeriesProvider");
                _movieDbSeriesProviderIsComplete =
                    movieDbSeriesProvider.GetMethod("IsComplete", BindingFlags.NonPublic | BindingFlags.Instance);
                _movieDbSeriesProviderImportData =
                    movieDbSeriesProvider.GetMethod("ImportData", BindingFlags.NonPublic | BindingFlags.Instance);
                _ensureSeriesInfo = movieDbSeriesProvider.GetMethod("EnsureSeriesInfo",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var seriesRootObject = movieDbSeriesProvider.GetNestedType("SeriesRootObject", BindingFlags.Public);
                _getTitleSeriesInfo = seriesRootObject.GetMethod("GetTitle");
                _nameSeriesInfoProperty = seriesRootObject.GetProperty("name");
                _alternativeTitleSeriesInfoProperty = seriesRootObject.GetProperty("alternative_titles");
                _alternativeTitleListProperty = _movieDbAssembly.GetType("MovieDb.TmdbAlternativeTitles")
                    .GetProperty("results");
                var tmdbTitleType = _movieDbAssembly.GetType("MovieDb.TmdbTitle");
                _alternativeTitle = tmdbTitleType.GetProperty("title");
                _alternativeTitleCountryCode = tmdbTitleType.GetProperty("iso_3166_1");
                _genresProperty = seriesRootObject.GetProperty("genres");
                _genreNameProperty = _movieDbAssembly.GetType("MovieDb.TmdbGenre").GetProperty("name");

                var movieDbSeasonProvider = _movieDbAssembly.GetType("MovieDb.MovieDbSeasonProvider");
                _movieDbSeasonProviderIsComplete =
                    movieDbSeasonProvider.GetMethod("IsComplete", BindingFlags.NonPublic | BindingFlags.Instance);
                _movieDbSeasonProviderImportData =
                    movieDbSeasonProvider.GetMethod("ImportData", BindingFlags.NonPublic | BindingFlags.Instance);
                var seasonRootObject = movieDbSeasonProvider.GetNestedType("SeasonRootObject", BindingFlags.Public);
                _nameSeasonInfoProperty = seasonRootObject.GetProperty("name");
                _overviewSeasonInfoProperty = seasonRootObject.GetProperty("overview");

                var movieDbEpisodeProvider = _movieDbAssembly.GetType("MovieDb.MovieDbEpisodeProvider");
                _movieDbEpisodeProviderIsComplete =
                    movieDbEpisodeProvider.GetMethod("IsComplete", BindingFlags.NonPublic | BindingFlags.Instance);
                _movieDbEpisodeProviderImportData =
                    movieDbEpisodeProvider.GetMethod("ImportData", BindingFlags.NonPublic | BindingFlags.Instance);
                var episodeRootObject = movieDbProviderBase.GetNestedType("RootObject", BindingFlags.Public);
                _nameEpisodeInfoProperty = episodeRootObject.GetProperty("name");
                _overviewEpisodeInfoProperty = episodeRootObject.GetProperty("overview");
                
                var getEpisodeInfo =
                    movieDbProviderBase.GetMethod("GetEpisodeInfo", BindingFlags.NonPublic | BindingFlags.Instance);
                _getEpisodeInfoAsync = AccessTools.AsyncMoveNext(getEpisodeInfo);
            }
            else
            {
                Plugin.Instance.Logger.Warn("ChineseMovieDb - MovieDb plugin is not installed");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                PatchTracker.IsSupported = false;
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _genericMovieDbInfoConstructor,
                prefix: nameof(GenericMovieDbInfoConstructorPrefix));

            if (!apply)
            {
                PatchUnpatch(Instance.PatchTracker, false, _genericMovieDbInfoProcessMainInfoMovie,
                    prefix: nameof(ProcessMainInfoMoviePrefix));
                PatchUnpatch(Instance.PatchTracker, false, _genericMovieDbInfoIsCompleteMovie,
                    prefix: nameof(IsCompletePrefix), postfix: nameof(IsCompletePostfix));
            }

            PatchUnpatch(PatchTracker, apply, _getMovieDbMetadataLanguages, postfix: nameof(MetadataLanguagesPostfix));
            PatchUnpatch(PatchTracker, apply, _getImageLanguagesParam, postfix: nameof(GetImageLanguagesParamPostfix));
            PatchUnpatch(PatchTracker, apply, _movieDbSeriesProviderIsComplete, prefix: nameof(IsCompletePrefix),
                postfix: nameof(IsCompletePostfix));
            PatchUnpatch(PatchTracker, apply, _movieDbSeriesProviderImportData, prefix: nameof(SeriesImportDataPrefix));
            PatchUnpatch(PatchTracker, apply, _ensureSeriesInfo, postfix: nameof(EnsureSeriesInfoPostfix));
            PatchUnpatch(PatchTracker, apply, _movieDbSeasonProviderIsComplete, prefix: nameof(IsCompletePrefix),
                postfix: nameof(IsCompletePostfix));
            PatchUnpatch(PatchTracker, apply, _movieDbSeasonProviderImportData, prefix: nameof(SeasonImportDataPrefix));
            PatchUnpatch(PatchTracker, apply, _movieDbEpisodeProviderIsComplete, prefix: nameof(IsCompletePrefix),
                postfix: nameof(IsCompletePostfix));
            PatchUnpatch(PatchTracker, apply, _movieDbEpisodeProviderImportData,
                prefix: nameof(EpisodeImportDataPrefix));
        }

        private void PatchCacheTime()
        {
            PatchUnpatch(PatchTracker, true, _getEpisodeInfoAsync, transpiler: nameof(GetEpisodeInfoAsyncTranspiler));
        }

        private static TimeSpan GetEpisodeCacheTime()
        {
            if (RefreshEpisodeTask.IsRunning || QueueManager.IsEpisodeRefreshProcessTaskRunning)
            {
                return TimeSpan.Zero;
            }

            return MetadataApi.DefaultCacheTime;
        }

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> GetEpisodeInfoAsyncTranspiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator)
        {
            var codeMatcher = new CodeMatcher(instructions, generator);

            codeMatcher.MatchStartForward(CodeMatch.LoadsField(_cacheTime))
                .ThrowIfInvalid("Could not find call to MovieDbProviderBase.CacheTime")
                .RemoveInstruction()
                .InsertAndAdvance(CodeInstruction.Call(typeof(ChineseMovieDb), nameof(GetEpisodeCacheTime)));

            return codeMatcher.Instructions();
        }

        private static bool IsUpdateNeeded(string currentValue, string newValue = null)
        {
            if (string.IsNullOrEmpty(currentValue)) return true;

            var isEpisodeName = newValue != null;
            var isJapaneseFallback = HasMovieDbJapaneseFallback();
            
            if (!isEpisodeName)
            {
                return !isJapaneseFallback ? !IsChinese(currentValue) : !IsChineseJapanese(currentValue);
            }

            if (!isJapaneseFallback)
            {
                if (!IsChinese(currentValue)) return true;

                return IsDefaultChineseEpisodeName(currentValue) && IsEnglish(newValue) &&
                       !IsDefaultEnglishEpisodeName(newValue);
            }

            if (!IsChineseJapanese(currentValue)) return true;

            if (IsDefaultChineseEpisodeName(currentValue))
            {
                if (IsJapanese(newValue) && !IsDefaultJapaneseEpisodeName(newValue)) return true;

                if (IsEnglish(newValue) && !IsDefaultEnglishEpisodeName(newValue)) return true;
            }

            return false;
        }

        [HarmonyPrefix]
        private static bool GenericMovieDbInfoConstructorPrefix(object __instance)
        {
            PatchUnpatch(Instance.PatchTracker, false, _genericMovieDbInfoProcessMainInfoMovie,
                prefix: nameof(ProcessMainInfoMoviePrefix), suppress: true);
            PatchUnpatch(Instance.PatchTracker, false, _genericMovieDbInfoIsCompleteMovie, prefix: nameof(IsCompletePrefix),
                postfix: nameof(IsCompletePostfix), suppress: true);
            PatchUnpatch(Instance.PatchTracker, true, _genericMovieDbInfoProcessMainInfoMovie,
                prefix: nameof(ProcessMainInfoMoviePrefix), suppress: true);
            PatchUnpatch(Instance.PatchTracker, true, _genericMovieDbInfoIsCompleteMovie, prefix: nameof(IsCompletePrefix),
                postfix: nameof(IsCompletePostfix), suppress: true);

            return true;
        }

        [HarmonyPrefix]
        private static bool ProcessMainInfoMoviePrefix(MetadataResult<Movie> resultItem, object settings,
            string preferredCountryCode, object movieData, bool isFirstLanguage)
        {
            var item = resultItem.Item;

            if (_getTitleMovieData != null && IsUpdateNeeded(item.Name))
            {
                item.Name = _getTitleMovieData.Invoke(movieData, null) as string;
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool IsCompletePrefix(BaseItem item, ref bool __result, out bool __state)
        {
            __state = false;

            var name = item.Name;
            var overview = item.Overview;
            var isJapaneseFallback = HasMovieDbJapaneseFallback();

            if (item is Movie || item is Series || item is Season)
            {
                __state = true;

                __result = !isJapaneseFallback
                    ? IsChinese(name) && IsChinese(overview)
                    : IsChineseJapanese(name) && IsChineseJapanese(overview);

                return false;
            }

            if (item is Episode)
            {
                __state = true;

                if (!isJapaneseFallback)
                {
                    if (IsChinese(name))
                    {
                        if (IsDefaultChineseEpisodeName(name))
                        {
                            __result = false;
                        }
                        else if (IsChinese(overview))
                        {
                            __result = true;
                        }
                        else
                        {
                            __result = false;
                        }
                    }
                    else
                    {
                        __result = false;
                    }
                }
                else
                {
                    if (IsChineseJapanese(name))
                    {
                        if (IsDefaultChineseEpisodeName(name))
                        {
                            __result = false;
                        }
                        else if (IsDefaultJapaneseEpisodeName(name))
                        {
                            __result = false;
                        }
                        else if (IsChineseJapanese(overview))
                        {
                            __result = true;
                        }
                        else
                        {
                            __result = false;
                        }
                    }
                    else
                    {
                        __result = false;
                    }
                }

                return false;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void IsCompletePostfix(BaseItem item, ref bool __result, bool __state)
        {
            if (__state)
            {
                if (IsChinese(item.Name))
                {
                    item.Name = ConvertTraditionalToSimplified(item.Name);
                }

                if (IsChinese(item.Overview))
                {
                    item.Overview = ConvertTraditionalToSimplified(item.Overview);
                }
                else if (BlockMovieDbNonFallbackLanguage(item.Overview))
                {
                    item.Overview = null;
                }
            }
        }

        [HarmonyPrefix]
        private static bool SeriesImportDataPrefix(MetadataResult<Series> seriesResult, object seriesInfo,
            string preferredCountryCode, object settings, bool isFirstLanguage)
        {
            var item = seriesResult.Item;

            if (_getTitleSeriesInfo != null && IsUpdateNeeded(item.Name))
            {
                item.Name = _getTitleSeriesInfo.Invoke(seriesInfo, null) as string;
            }

            if (_genresProperty != null && _genreNameProperty != null && isFirstLanguage &&
                string.Equals(CurrentLookupLanguageCountryCode.Value, "CN", StringComparison.OrdinalIgnoreCase))
            {
                if (_genresProperty.GetValue(seriesInfo) is IList genres)
                {
                    foreach (var genre in genres)
                    {
                        var genreValue = _genreNameProperty.GetValue(genre)?.ToString();
                        if (!string.IsNullOrEmpty(genreValue))
                        {
                            if (string.Equals(genreValue, "Sci-Fi & Fantasy",
                                    StringComparison.OrdinalIgnoreCase))
                                _genreNameProperty.SetValue(genre, "科幻奇幻");

                            if (string.Equals(genreValue, "War & Politics",
                                    StringComparison.OrdinalIgnoreCase))
                                _genreNameProperty.SetValue(genre, "战争政治");
                        }
                    }
                }
            }

            return true;
        }

        [HarmonyPostfix]
        private static void EnsureSeriesInfoPostfix(string tmdbId, string language, CancellationToken cancellationToken,
            Task __result)
        {
            if (WasCalledByMethod(_movieDbAssembly, "FetchImages")) return;

            var lookupLanguageCountryCode = !string.IsNullOrEmpty(language) && language.Contains('-')
                ? language.Split('-')[1]
                : null;

            CurrentLookupLanguageCountryCode.Value = lookupLanguageCountryCode;

            if (_seriesInfoTaskResultProperty == null)
                _seriesInfoTaskResultProperty = __result.GetType().GetProperty("Result");

            var seriesInfo = _seriesInfoTaskResultProperty?.GetValue(__result);
            if (seriesInfo != null && _nameSeriesInfoProperty != null)
            {
                var name = _nameSeriesInfoProperty.GetValue(seriesInfo) as string;

                if (!HasMovieDbJapaneseFallback()
                        ? !IsChinese(name)
                        : !IsChineseJapanese(name) &&
                          _alternativeTitleSeriesInfoProperty != null &&
                          _alternativeTitleListProperty != null &&
                          _alternativeTitleCountryCode != null && _alternativeTitle != null)
                {
                    var alternativeTitles = _alternativeTitleSeriesInfoProperty.GetValue(seriesInfo);
                    if (_alternativeTitleListProperty.GetValue(alternativeTitles) is IList altTitles)
                    {
                        foreach (var altTitle in altTitles)
                        {
                            var iso3166Value = _alternativeTitleCountryCode.GetValue(altTitle)?.ToString();
                            var titleValue = _alternativeTitle.GetValue(altTitle)?.ToString();
                            if (!string.IsNullOrEmpty(iso3166Value) && !string.IsNullOrEmpty(titleValue) &&
                                lookupLanguageCountryCode != null && string.Equals(iso3166Value,
                                    lookupLanguageCountryCode, StringComparison.OrdinalIgnoreCase))
                            {
                                _nameSeriesInfoProperty.SetValue(seriesInfo, titleValue);
                                break;
                            }
                        }
                    }
                }
            }
        }

        [HarmonyPrefix]
        private static bool SeasonImportDataPrefix(Season item, object seasonInfo, string name, int seasonNumber,
            bool isFirstLanguage)
        {
            if (_nameSeasonInfoProperty != null && IsUpdateNeeded(item.Name))
            {
                item.Name = _nameSeasonInfoProperty.GetValue(seasonInfo) as string;
            }

            if (_overviewSeasonInfoProperty != null && IsUpdateNeeded(item.Overview))
            {
                item.Overview = _overviewSeasonInfoProperty.GetValue(seasonInfo) as string;
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool EpisodeImportDataPrefix(MetadataResult<Episode> result, EpisodeInfo info, object response,
            object settings, bool isFirstLanguage)
        {
            var item = result.Item;

            if (_nameEpisodeInfoProperty != null && _nameEpisodeInfoProperty.GetValue(response) is string nameValue &&
                IsUpdateNeeded(item.Name, nameValue))
            {
                item.Name = nameValue;
            }

            if (_overviewEpisodeInfoProperty != null && IsUpdateNeeded(item.Overview))
            {
                item.Overview = _overviewEpisodeInfoProperty.GetValue(response) as string;
            }

            return true;
        }

        [HarmonyReversePatch]
        private static string MapLanguageToProviderLanguageStub(object instance, string language, string country,
            bool exactMatchOnly, string[] providerLanguages) => throw new NotImplementedException();

        [HarmonyPostfix]
        private static void MetadataLanguagesPostfix(object __instance, ItemLookupInfo searchInfo,
            string[] providerLanguages, ref string[] __result)
        {
            if (searchInfo.MetadataLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                var list = __result.ToList();
                var index = list.FindIndex(l => string.Equals(l, "en", StringComparison.OrdinalIgnoreCase) ||
                                                string.Equals(l, "en-us", StringComparison.OrdinalIgnoreCase));

                var currentFallbackLanguages = GetMovieDbFallbackLanguages();

                foreach (var fallbackLanguage in currentFallbackLanguages)
                {
                    if (!list.Contains(fallbackLanguage, StringComparer.OrdinalIgnoreCase))
                    {
                        var mappedLanguage = MapLanguageToProviderLanguageStub(__instance, fallbackLanguage, null, false,
                            providerLanguages);

                        if (!string.IsNullOrEmpty(mappedLanguage))
                        {
                            if (index >= 0)
                            {
                                list.Insert(index, mappedLanguage);
                                index++;
                            }
                            else
                            {
                                list.Add(mappedLanguage);
                            }
                        }
                    }
                }

                __result = list.ToArray();
            }
        }

        [HarmonyPostfix]
        private static void GetImageLanguagesParamPostfix(ref string __result)
        {
            var list = __result.Split(',').ToList();

            if (list.Any(i => i.StartsWith("zh")) && !list.Contains("zh"))
            {
                list.Insert(list.FindIndex(i => i.StartsWith("zh")) + 1, "zh");
            }

            __result = string.Join(",", list.ToArray());
        }
    }
}
