using HarmonyLib;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using static StrmAssistant.Common.CommonUtility;
using static StrmAssistant.Mod.PatchManager;
using HttpRequestOptions = MediaBrowser.Common.Net.HttpRequestOptions;

namespace StrmAssistant.Mod
{
    public class AltMovieDbConfig : PatchBase<AltMovieDbConfig>
    {
        private static Assembly _movieDbAssembly;
        private static MethodInfo _getMovieDbResponse;
        private static MethodInfo _saveImageFromRemoteUrl;
        private static MethodInfo _downloadImage;
        private static MethodInfo _createHttpClientHandler;

        private static readonly string DefaultMovieDbApiUrl = "https://api.themoviedb.org";
        private static readonly string DefaultAltMovieDbApiUrl = "https://api.tmdb.org";
        private static readonly string DefaultMovieDbImageUrl = "https://image.tmdb.org";
        private static string SystemDefaultMovieDbApiKey;

        public static string CurrentMovieDbApiKey
        {
            get
            {
                var options = Plugin.Instance.MetadataEnhanceStore.GetOptions();
                return IsValidMovieDbApiKey(options.AltMovieDbApiKey)
                    ? options.AltMovieDbApiKey
                    : SystemDefaultMovieDbApiKey;
            }
        }

        public static string CurrentMovieDbApiUrl
        {
            get
            {
                var options = Plugin.Instance.MetadataEnhanceStore.GetOptions();
                return IsValidHttpUrl(options.AltMovieDbApiUrl) ? options.AltMovieDbApiUrl : DefaultMovieDbApiUrl;
            }
        }

        public static string CurrentMovieDbImageUrl
        {
            get
            {
                var options = Plugin.Instance.MetadataEnhanceStore.GetOptions();
                return IsValidHttpUrl(options.AltMovieDbImageUrl) ? options.AltMovieDbImageUrl : DefaultMovieDbImageUrl;
            }
        }
        
        public AltMovieDbConfig()
        {
            Initialize();

            if (Plugin.Instance.MetadataEnhanceStore.GetOptions().AltMovieDbConfig)
            {
                PatchApiUrl();

                if (!string.IsNullOrEmpty(Plugin.Instance.MetadataEnhanceStore.GetOptions().AltMovieDbImageUrl))
                {
                    PatchImageUrl();
                }
            }
        }

        protected override void OnInitialize()
        {
            _movieDbAssembly = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "MovieDb");

            if (_movieDbAssembly != null)
            {
                var movieDbProviderBase = _movieDbAssembly.GetType("MovieDb.MovieDbProviderBase");
                _getMovieDbResponse = movieDbProviderBase.GetMethod("GetMovieDbResponse",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                var apiKey = movieDbProviderBase.GetField("ApiKey", BindingFlags.Static | BindingFlags.NonPublic);
                SystemDefaultMovieDbApiKey = apiKey?.GetValue(null) as string;

                var embyProviders = Assembly.Load("Emby.Providers");
                var providerManager = embyProviders.GetType("Emby.Providers.Manager.ProviderManager");
                _saveImageFromRemoteUrl = providerManager.GetMethod("SaveImageFromRemoteUrl",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                var embyApi = Assembly.Load("Emby.Api");
                var remoteImageService = embyApi.GetType("Emby.Api.Images.RemoteImageService");
                _downloadImage = remoteImageService.GetMethod("DownloadImage",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                var embyServerImplementationsAssembly = Assembly.Load("Emby.Server.Implementations");
                var applicationHost =
                    embyServerImplementationsAssembly.GetType("Emby.Server.Implementations.ApplicationHost");
                _createHttpClientHandler = applicationHost.GetMethod("CreateHttpClientHandler",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }
            else
            {
                Plugin.Instance.Logger.Info("AltMovieDbConfig - MovieDb plugin is not installed");
                PatchTracker.FallbackPatchApproach = PatchApproach.None;
                PatchTracker.IsSupported = false;
            }
        }

        protected override void Prepare(bool apply)
        {
            // No action needed
        }

        private void PrepareApiUrl(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _getMovieDbResponse, prefix: nameof(GetMovieDbResponsePrefix));
            PatchUnpatch(PatchTracker, apply, _createHttpClientHandler,
                postfix: nameof(CreateHttpClientHandlerPostfix));
        }

        public void PatchApiUrl() => PrepareApiUrl(true);

        public void UnpatchApiUrl() => PrepareApiUrl(false);

        private void PrepareImageUrl(bool apply)
        {
            PatchUnpatch(PatchTracker, apply, _saveImageFromRemoteUrl, prefix: nameof(SaveImageFromRemoteUrlPrefix));
            PatchUnpatch(PatchTracker, apply, _downloadImage, prefix: nameof(DownloadImagePrefix));
        }

        public void PatchImageUrl() => PrepareImageUrl(true);

        public void UnpatchImageUrl() => PrepareImageUrl(false);

        [HarmonyPostfix]
        private static void CreateHttpClientHandlerPostfix(ref HttpMessageHandler __result)
        {
            switch (__result)
            {
                case HttpClientHandler httpClientHandler:
                    httpClientHandler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    break;
                case SocketsHttpHandler socketsHttpHandler:
                    socketsHttpHandler.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                    break;
            }
        }

        [HarmonyPrefix]
        private static bool GetMovieDbResponsePrefix(HttpRequestOptions options)
        {
            var metadataEnhanceOptions = Plugin.Instance.MetadataEnhanceStore.GetOptions();
            var apiUrl = metadataEnhanceOptions.AltMovieDbApiUrl;
            var apiKey = metadataEnhanceOptions.AltMovieDbApiKey;

            var requestUrl = options.Url;

            if (requestUrl.StartsWith(DefaultMovieDbApiUrl + "/3/configuration", StringComparison.Ordinal))
            {
                requestUrl = requestUrl.Replace(DefaultMovieDbApiUrl, DefaultAltMovieDbApiUrl);
            }
            else if (IsValidHttpUrl(apiUrl))
            {
                requestUrl = requestUrl.Replace(DefaultMovieDbApiUrl, apiUrl);
            }

            if (IsValidMovieDbApiKey(apiKey))
            {
                requestUrl = requestUrl.Replace(SystemDefaultMovieDbApiKey, apiKey);
            }

            if (!string.Equals(requestUrl, options.Url, StringComparison.Ordinal))
            {
                options.Url = requestUrl;
            }

            return true;
        }

        private static void ReplaceMovieDbImageUrl(ref string url)
        {
            var imageUrl = Plugin.Instance.MetadataEnhanceStore.GetOptions().AltMovieDbImageUrl;

            if (IsValidHttpUrl(imageUrl))
            {
                url = url.Replace(DefaultMovieDbImageUrl, imageUrl);
            }
        }

        [HarmonyPrefix]
        private static bool SaveImageFromRemoteUrlPrefix(BaseItem item, LibraryOptions libraryOptions, ref string url,
            ImageType type, int? imageIndex, long[] generatedFromItemIds, IDirectoryService directoryService,
            bool updateImageCache, CancellationToken cancellationToken)
        {
            ReplaceMovieDbImageUrl(ref url);

            return true;
        }

        [HarmonyPrefix]
        private static bool DownloadImagePrefix(ref string url, Guid urlHash, string pointerCachePath,
            CancellationToken cancellationToken)
        {
            ReplaceMovieDbImageUrl(ref url);

            return true;
        }
    }
}
