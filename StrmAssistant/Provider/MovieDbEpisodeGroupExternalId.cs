using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using static StrmAssistant.Common.CommonUtility;

namespace StrmAssistant.Provider
{
    public class MovieDbEpisodeGroupExternalId : IExternalId
    {
        private string _tmdbId;
        private string _episodeGroupId;

        public string Name => "MovieDb Episode Group";

        public string Key => StaticName;

        public string UrlFormatString => IsValidHttpUrl(_episodeGroupId) ? _episodeGroupId :
            !string.IsNullOrWhiteSpace(_tmdbId) ? $"https://www.themoviedb.org/tv/{_tmdbId}/episode_group/{{0}}" : null;

        public bool Supports(IHasProviderIds item)
        {
            _tmdbId = item.GetProviderId(MetadataProviders.Tmdb);
            _episodeGroupId = item.GetProviderId(StaticName);

            return item is Series;
        }

        public static string StaticName => "TmdbEg";
    }
}
