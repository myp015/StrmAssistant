using HarmonyLib;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using StrmAssistant.Common;
using StrmAssistant.Provider;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Mod.PatchManager;

namespace StrmAssistant.Mod
{
    public class OptimizeMovieDbEpisodeScraping : PatchBase<OptimizeMovieDbEpisodeScraping>
    {
        private static Assembly _movieDbAssembly;
        private static MethodInfo _episodeGetMetadata;
        private static MethodInfo _episodeGetImages;

        public OptimizeMovieDbEpisodeScraping()
        {
            Initialize();

            var options = Plugin.Instance.MetadataEnhanceStore.GetOptions();
            if (options.OptimizeMovieDbEpisodeScraping || options.DisableEpisodeImageScraping)
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
                var movieDbEpisodeProvider = _movieDbAssembly.GetType("MovieDb.MovieDbEpisodeProvider");
                _episodeGetMetadata = movieDbEpisodeProvider.GetMethod("GetMetadata",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(RemoteMetadataFetchOptions<EpisodeInfo>), typeof(CancellationToken) }, null);

                var movieDbEpisodeImageProvider = _movieDbAssembly.GetType("MovieDb.MovieDbEpisodeImageProvider");
                _episodeGetImages = movieDbEpisodeImageProvider.GetMethod("GetImages",
                    BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(RemoteImageFetchOptions), typeof(CancellationToken) }, null);
            }
            else
            {
                Plugin.Instance.Logger.Warn("OptimizeMovieDbEpisodeScraping - MovieDb plugin is not installed");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                PatchTracker.IsSupported = false;
            }
        }

        protected override void Prepare(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _episodeGetMetadata, prefix: nameof(EpisodeGetMetadataPrefix));
            PatchUnpatch(PatchTracker, apply, _episodeGetImages, prefix: nameof(EpisodeGetImagesPrefix));
        }

        [HarmonyPrefix]
        private static bool EpisodeGetMetadataPrefix(RemoteMetadataFetchOptions<EpisodeInfo> options,
            CancellationToken cancellationToken, ref Task<MetadataResult<Episode>> __result)
        {
            if (!Plugin.Instance.MetadataEnhanceStore.GetOptions().OptimizeMovieDbEpisodeScraping)
            {
                return true;
            }

            var outcome = Task.Run(() => GetEpisodeMetadataFromSeasonAsync(options, cancellationToken), cancellationToken)
                .Result;

            if (outcome.Result != null)
            {
                __result = Task.FromResult(outcome.Result);
                return false;
            }

            if (outcome.SuppressFallback)
            {
                __result = Task.FromResult(new MetadataResult<Episode> { HasMetadata = false });
                return false;
            }

            return true;
        }

        [HarmonyPrefix]
        private static bool EpisodeGetImagesPrefix(RemoteImageFetchOptions options,
            CancellationToken cancellationToken, ref Task<IEnumerable<RemoteImageInfo>> __result)
        {
            var enhanceOptions = Plugin.Instance.MetadataEnhanceStore.GetOptions();

            // 一键忽略所有集图片：直接短路，跳过按季和逐集请求
            if (enhanceOptions.DisableEpisodeImageScraping)
            {
                __result = Task.FromResult<IEnumerable<RemoteImageInfo>>(Array.Empty<RemoteImageInfo>());
                return false;
            }

            if (!enhanceOptions.OptimizeMovieDbEpisodeScraping)
            {
                return true;
            }

            var outcome = Task.Run(() => GetEpisodeImagesFromSeasonAsync(options, cancellationToken), cancellationToken)
                .Result;

            if (outcome.Result != null)
            {
                __result = Task.FromResult(outcome.Result);
                return false;
            }

            if (outcome.SuppressFallback)
            {
                __result = Task.FromResult<IEnumerable<RemoteImageInfo>>(Array.Empty<RemoteImageInfo>());
                return false;
            }

            return true;
        }

        private readonly struct PrefixOutcome<T> where T : class
        {
            public PrefixOutcome(T result, bool suppressFallback)
            {
                Result = result;
                SuppressFallback = suppressFallback;
            }

            public T Result { get; }
            public bool SuppressFallback { get; }

            public static PrefixOutcome<T> Passthrough => new PrefixOutcome<T>(null, false);
            public static PrefixOutcome<T> Suppress => new PrefixOutcome<T>(null, true);
            public static PrefixOutcome<T> From(T value) => new PrefixOutcome<T>(value, false);
        }

        private static async Task<PrefixOutcome<MetadataResult<Episode>>> GetEpisodeMetadataFromSeasonAsync(
            RemoteMetadataFetchOptions<EpisodeInfo> options, CancellationToken cancellationToken)
        {
            var episodeInfo = options?.SearchInfo;

            if (episodeInfo is null || !episodeInfo.ParentIndexNumber.HasValue ||
                !episodeInfo.IndexNumber.HasValue)
            {
                return PrefixOutcome<MetadataResult<Episode>>.Passthrough;
            }

            if (episodeInfo.SeriesProviderIds is null ||
                !episodeInfo.SeriesProviderIds.TryGetValue(MetadataProviders.Tmdb.ToString(), out var tmdbId) ||
                string.IsNullOrEmpty(tmdbId))
            {
                return PrefixOutcome<MetadataResult<Episode>>.Passthrough;
            }

            var seasonInfo = await FetchSeasonInfoAsync(tmdbId, episodeInfo.ParentIndexNumber.Value,
                    episodeInfo.MetadataLanguage, cancellationToken)
                .ConfigureAwait(false);

            var episodeResponse = seasonInfo?.episodes?
                .FirstOrDefault(e => e.episode_number == episodeInfo.IndexNumber.Value);

            if (episodeResponse is null)
            {
                // 季数据已成功获取但未匹配到该集：按用户开关决定是否回退到逐集请求
                if (seasonInfo != null &&
                    Plugin.Instance.MetadataEnhanceStore.GetOptions().DisableMovieDbEpisodeScrapingFallback)
                {
                    return PrefixOutcome<MetadataResult<Episode>>.Suppress;
                }

                return PrefixOutcome<MetadataResult<Episode>>.Passthrough;
            }

            var result = new MetadataResult<Episode>
            {
                HasMetadata = true,
                Item = new Episode
                {
                    IndexNumber = episodeResponse.episode_number,
                    ParentIndexNumber = seasonInfo.season_number,
                    Name = episodeResponse.name,
                    Overview = episodeResponse.overview,
                    PremiereDate = episodeResponse.air_date,
                    ProductionYear = episodeResponse.air_date.Year
                }
            };

            if (episodeResponse.id > 0)
            {
                result.Item.SetProviderId(MetadataProviders.Tmdb.ToString(),
                    episodeResponse.id.ToString(CultureInfo.InvariantCulture));
            }

            return PrefixOutcome<MetadataResult<Episode>>.From(result);
        }

        private static async Task<PrefixOutcome<IEnumerable<RemoteImageInfo>>> GetEpisodeImagesFromSeasonAsync(
            RemoteImageFetchOptions options, CancellationToken cancellationToken)
        {
            if (!(options?.Item is Episode episode) || !episode.ParentIndexNumber.HasValue ||
                !episode.IndexNumber.HasValue || episode.Series is null)
            {
                return PrefixOutcome<IEnumerable<RemoteImageInfo>>.Passthrough;
            }

            var tmdbId = episode.Series.GetProviderId(MetadataProviders.Tmdb);
            if (string.IsNullOrEmpty(tmdbId)) return PrefixOutcome<IEnumerable<RemoteImageInfo>>.Passthrough;

            var seasonInfo = await FetchSeasonInfoAsync(tmdbId, episode.ParentIndexNumber.Value,
                    episode.GetPreferredMetadataLanguage(), cancellationToken)
                .ConfigureAwait(false);

            var episodeResponse = seasonInfo?.episodes?
                .FirstOrDefault(e => e.episode_number == episode.IndexNumber.Value);

            if (string.IsNullOrEmpty(episodeResponse?.still_path))
            {
                // 季数据已成功获取但未匹配到该集图片：按用户开关决定是否回退到逐集请求
                if (seasonInfo != null &&
                    Plugin.Instance.MetadataEnhanceStore.GetOptions().DisableMovieDbEpisodeScrapingFallback)
                {
                    return PrefixOutcome<IEnumerable<RemoteImageInfo>>.Suppress;
                }

                return PrefixOutcome<IEnumerable<RemoteImageInfo>>.Passthrough;
            }

            var stillPath = episodeResponse.still_path.TrimStart('/');
            var imageUrl = $"{AltMovieDbConfig.CurrentMovieDbImageUrl}/t/p/original/{stillPath}";

            return PrefixOutcome<IEnumerable<RemoteImageInfo>>.From(new[]
            {
                new RemoteImageInfo
                {
                    ProviderName = "TheMovieDb",
                    Url = imageUrl,
                    Type = ImageType.Primary
                }
            });
        }

        private static async Task<SeasonResponseInfo> FetchSeasonInfoAsync(string tmdbId, int seasonNumber,
            string language, CancellationToken cancellationToken)
        {
            var cacheFilename = $"season-{seasonNumber}-episodes";
            if (!string.IsNullOrEmpty(language)) cacheFilename = cacheFilename + "-" + language;
            var cacheKey = $"tmdb_season_episodes_{tmdbId}_{seasonNumber}_{language}";
            var cachePath = Path.Combine(Plugin.Instance.ApplicationPaths.CachePath, "tmdb-tv", tmdbId,
                cacheFilename + ".json");
            var seasonUrl = MetadataApi.BuildMovieDbApiUrl($"tv/{tmdbId}/season/{seasonNumber}", language);

            return await Plugin.MetadataApi
                .GetMovieDbResponse<SeasonResponseInfo>(seasonUrl, cacheKey, cachePath, cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
