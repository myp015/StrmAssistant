using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using StrmAssistant.Common;
using StrmAssistant.ScheduledTask;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
        private static MethodInfo _baseImportData; 
        private static MethodInfo _movieGetMetadata;
        private static MethodInfo _getTitleMovieData;
        private static MethodInfo _getMovieDbMetadataLanguages;
        private static MethodInfo _mapLanguageToProviderLanguage;
        private static MethodInfo _getImageLanguagesParam;
        private static MethodInfo _movieDbSeriesProviderIsComplete;
        private static MethodInfo _movieDbSeasonProviderIsComplete;
        private static MethodInfo _movieDbEpisodeProviderIsComplete;
        private static MethodInfo _getTitleSeriesInfo;
        private static MethodInfo _genericProcessMainInfoMovie;
        private static MethodInfo _genericIsCompleteMovie;
        private static MethodInfo _getEpisodeInfoAsync;
        private static FieldInfo _cacheTime;
        private static readonly object _lock = new object();

        public ChineseMovieDb()
        {
            Initialize();
            PatchCacheTime();
            if (Plugin.Instance.MetadataEnhanceStore.GetOptions().ChineseMovieDb) Patch();
        }

        protected override void OnInitialize()
        {
            _movieDbAssembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "MovieDb");
            if (_movieDbAssembly == null) return;

            var movieDbProviderBase = _movieDbAssembly.GetType("MovieDb.MovieDbProviderBase");
            
            // 适配 4.9.1.90: 5参数 ImportData
            _baseImportData = movieDbProviderBase?.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "ImportData" && m.GetParameters().Length == 5);
            
            _getMovieDbMetadataLanguages = movieDbProviderBase?.GetMethod("GetMovieDbMetadataLanguages", BindingFlags.Public | BindingFlags.Instance);
            _mapLanguageToProviderLanguage = movieDbProviderBase?.GetMethod("MapLanguageToProviderLanguage", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_mapLanguageToProviderLanguage != null) ReversePatch(PatchTracker, _mapLanguageToProviderLanguage, nameof(MapLanguageToProviderLanguageStub));
            
            _getImageLanguagesParam = movieDbProviderBase?.GetMethod("GetImageLanguagesParam", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string[]) }, null);
            _cacheTime = movieDbProviderBase?.GetField("CacheTime", BindingFlags.Public | BindingFlags.Static);

            var movieDbProvider = _movieDbAssembly.GetType("MovieDb.MovieDbProvider");
            _movieGetMetadata = movieDbProvider?.GetMethod("GetMetadata");
            var completeMovieData = movieDbProvider?.GetNestedType("CompleteMovieData", BindingFlags.NonPublic);
            _getTitleMovieData = completeMovieData?.GetMethod("GetTitle");
            if (_getTitleMovieData != null) ReversePatch(PatchTracker, _getTitleMovieData, nameof(MovieGetTitleStub));

            var genericInfoType = _movieDbAssembly.GetType("MovieDb.GenericMovieDbInfo`1");
            if (genericInfoType != null) {
                var genericMovieDbInfoMovie = genericInfoType.MakeGenericType(typeof(Movie));
                _genericIsCompleteMovie = genericMovieDbInfoMovie.GetMethod("IsComplete", BindingFlags.NonPublic | BindingFlags.Instance);
                _genericProcessMainInfoMovie = genericMovieDbInfoMovie.GetMethod("ProcessMainInfo", BindingFlags.NonPublic | BindingFlags.Instance);
            }

            _movieDbSeriesProviderIsComplete = _movieDbAssembly.GetType("MovieDb.MovieDbSeriesProvider")?.GetMethod("IsComplete", BindingFlags.NonPublic | BindingFlags.Instance);
            var seriesRoot = _movieDbAssembly.GetType("MovieDb.MovieDbSeriesProvider")?.GetNestedType("SeriesRootObject", BindingFlags.Public);
            _getTitleSeriesInfo = seriesRoot?.GetMethod("GetTitle");
            if (_getTitleSeriesInfo != null) ReversePatch(PatchTracker, _getTitleSeriesInfo, nameof(SeriesGetTitleStub));

            _movieDbSeasonProviderIsComplete = _movieDbAssembly.GetType("MovieDb.MovieDbSeasonProvider")?.GetMethod("IsComplete", BindingFlags.NonPublic | BindingFlags.Instance);
            _movieDbEpisodeProviderIsComplete = _movieDbAssembly.GetType("MovieDb.MovieDbEpisodeProvider")?.GetMethod("IsComplete", BindingFlags.NonPublic | BindingFlags.Instance);
            
            var getEpInfo = movieDbProviderBase?.GetMethod("GetEpisodeInfo", BindingFlags.NonPublic | BindingFlags.Instance);
            if (getEpInfo != null) _getEpisodeInfoAsync = AccessTools.AsyncMoveNext(getEpInfo);
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _getMovieDbMetadataLanguages, postfix: nameof(MetadataLanguagesPostfix));
            PatchUnpatch(PatchTracker, apply, _getImageLanguagesParam, postfix: nameof(GetImageLanguagesParamPostfix));

            if (_baseImportData != null)
                PatchUnpatch(PatchTracker, apply, _baseImportData, prefix: nameof(UniversalImportDataPrefix));

            PatchUnpatch(PatchTracker, apply, _movieGetMetadata, prefix: nameof(MovieGetMetadataPrefix));
            PatchUnpatch(PatchTracker, apply, _genericProcessMainInfoMovie, prefix: nameof(ProcessMainInfoMoviePrefix));
            PatchUnpatch(PatchTracker, apply, _genericIsCompleteMovie, prefix: nameof(IsCompletePrefix), postfix: nameof(IsCompletePostfix));

            if (_movieDbSeriesProviderIsComplete != null) PatchUnpatch(PatchTracker, apply, _movieDbSeriesProviderIsComplete, prefix: nameof(IsCompletePrefix), postfix: nameof(IsCompletePostfix));
            if (_movieDbSeasonProviderIsComplete != null) PatchUnpatch(PatchTracker, apply, _movieDbSeasonProviderIsComplete, prefix: nameof(IsCompletePrefix), postfix: nameof(IsCompletePostfix));
            if (_movieDbEpisodeProviderIsComplete != null) PatchUnpatch(PatchTracker, apply, _movieDbEpisodeProviderIsComplete, prefix: nameof(IsCompletePrefix), postfix: nameof(IsCompletePostfix));
        }

        [HarmonyPrefix]
        private static void UniversalImportDataPrefix(object result, object response)
        {
            if (result is MetadataResult<Series> seriesResult) SeriesImportDataLogic(seriesResult, response);
            else if (result is Season season) SeasonImportDataLogic(season, response);
            else if (result is MetadataResult<Episode> episodeResult) EpisodeImportDataLogic(episodeResult, response);
        }

        private static void SeriesImportDataLogic(MetadataResult<Series> res, object resp) {
            if (IsUpdateNeeded(res.Item.Name)) res.Item.Name = SeriesGetTitleStub(resp);
            var ov = Traverse.Create(resp).Property("overview").GetValue<string>();
            if (IsUpdateNeeded(res.Item.Overview) && !string.IsNullOrEmpty(ov)) res.Item.Overview = WebUtility.HtmlDecode(ov).Replace("\n\n", "\n");
        }

        private static void SeasonImportDataLogic(Season item, object resp) {
            if (IsUpdateNeeded(item.Name)) item.Name = Traverse.Create(resp).Property("name").GetValue<string>();
            if (IsUpdateNeeded(item.Overview)) item.Overview = Traverse.Create(resp).Property("overview").GetValue<string>();
        }

        private static void EpisodeImportDataLogic(MetadataResult<Episode> res, object resp) {
            var n = Traverse.Create(resp).Property("name").GetValue<string>();
            if (IsUpdateNeeded(res.Item.Name, n)) res.Item.Name = n;
            if (IsUpdateNeeded(res.Item.Overview)) res.Item.Overview = Traverse.Create(resp).Property("overview").GetValue<string>();
        }

        [HarmonyReversePatch] private static string MovieGetTitleStub(object instance) => throw new NotImplementedException();
        [HarmonyReversePatch] private static string SeriesGetTitleStub(object instance) => throw new NotImplementedException();
        [HarmonyReversePatch] private static string MapLanguageToProviderLanguageStub(object instance, string language, string country, bool exact, string[] providerLanguages) => throw new NotImplementedException();

        [HarmonyPrefix]
        private static void MovieGetMetadataPrefix() {
            lock (_lock) {
                PatchUnpatch(Instance.PatchTracker, true, _genericProcessMainInfoMovie, prefix: nameof(ProcessMainInfoMoviePrefix), suppress: true);
                PatchUnpatch(Instance.PatchTracker, true, _genericIsCompleteMovie, prefix: nameof(IsCompletePrefix), postfix: nameof(IsCompletePostfix), suppress: true);
            }
        }

        [HarmonyPrefix]
        private static void ProcessMainInfoMoviePrefix(object resultItem, object movieData) {
            if (resultItem is MetadataResult<Movie> res) {
                if (IsUpdateNeeded(res.Item.Name)) res.Item.Name = MovieGetTitleStub(movieData);
                var ov = Traverse.Create(movieData).Property("overview").GetValue<string>();
                if (IsUpdateNeeded(res.Item.Overview) && !string.IsNullOrEmpty(ov)) res.Item.Overview = WebUtility.HtmlDecode(ov).Replace("\n\n", "\n");
            }
        }

        [HarmonyPrefix]
        private static bool IsCompletePrefix(BaseItem item, ref bool __result, out bool __state) {
            __state = true;
            bool isJp = HasMovieDbJapaneseFallback();
            if (item is Movie || item is Series || item is Season)
                __result = !isJp ? IsChinese(item.Name) && IsChinese(item.Overview) : IsChineseJapanese(item.Name) && IsChineseJapanese(item.Overview);
            else if (item is Episode)
                __result = !IsDefaultChineseEpisodeName(item.Name) && (!isJp ? IsChinese(item.Overview) : IsChineseJapanese(item.Overview));
            else __state = false;
            return !__state;
        }

        [HarmonyPostfix]
        private static void IsCompletePostfix(BaseItem item, bool __state) {
            if (__state) {
                if (IsChinese(item.Name)) item.Name = ConvertTraditionalToSimplified(item.Name);
                if (IsChinese(item.Overview)) item.Overview = ConvertTraditionalToSimplified(item.Overview);
                if (!string.IsNullOrEmpty(item.Tagline)) item.Tagline = null;
            }
        }

        [HarmonyPostfix]
        private static void MetadataLanguagesPostfix(object __instance, ItemLookupInfo searchInfo, string[] providerLanguages, ref string[] __result) {
            if (searchInfo.MetadataLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) {
                var list = __result.ToList();
                int idx = list.FindIndex(l => l.StartsWith("en", StringComparison.OrdinalIgnoreCase));
                foreach (var fb in GetMovieDbFallbackLanguages()) {
                    if (list.Contains(fb, StringComparer.OrdinalIgnoreCase)) continue;
                    var m = MapLanguageToProviderLanguageStub(__instance, fb, null, false, providerLanguages);
                    if (!string.IsNullOrEmpty(m)) { if (idx >= 0) list.Insert(idx++, m); else list.Add(m); }
                }
                __result = list.ToArray();
            }
        }

        [HarmonyPostfix]
        private static void GetImageLanguagesParamPostfix(ref string __result) {
            var list = __result.Split(',').ToList();
            if (list.Any(i => i.StartsWith("zh")) && !list.Contains("zh")) list.Insert(list.FindIndex(i => i.StartsWith("zh")) + 1, "zh");
            __result = string.Join(",", list);
        }

        private void PatchCacheTime() { if (_getEpisodeInfoAsync != null) PatchUnpatch(PatchTracker, true, _getEpisodeInfoAsync, transpiler: nameof(GetEpisodeInfoAsyncTranspiler)); }
        private static TimeSpan GetEpisodeCacheTime() => (RefreshEpisodeTask.IsRunning || QueueManager.IsEpisodeRefreshProcessTaskRunning) ? TimeSpan.Zero : MetadataApi.DefaultCacheTime;
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> GetEpisodeInfoAsyncTranspiler(IEnumerable<CodeInstruction> ins, ILGenerator gen) {
            var cm = new CodeMatcher(ins, gen);
            if (_cacheTime == null) return ins;
            return cm.MatchStartForward(CodeMatch.LoadsField(_cacheTime)).RemoveInstruction().InsertAndAdvance(CodeInstruction.Call(typeof(ChineseMovieDb), nameof(GetEpisodeCacheTime))).Instructions();
        }

        private static bool IsUpdateNeeded(string cur, string n = null) {
            if (string.IsNullOrEmpty(cur)) return true;
            bool isJp = HasMovieDbJapaneseFallback();
            if (n == null) return !isJp ? !IsChinese(cur) : !IsChineseJapanese(cur);
            return IsDefaultChineseEpisodeName(cur) && ((IsChinese(n) && !IsDefaultChineseEpisodeName(n)) || (isJp && IsJapanese(n) && !IsDefaultJapaneseEpisodeName(n)));
        }
    }
}