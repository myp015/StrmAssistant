using System;
using System.Collections.Generic;

namespace StrmAssistant.Provider
{
    public class EpisodeGroupResponse
    {
        public string description { get; set; }
        public List<EpisodeGroup> groups { get; set; }
        public string id { get; set; }
    }

    public class EpisodeGroup
    {
        public string name { get; set; }
        public int order { get; set; }
        public List<GroupEpisode> episodes { get; set; }
    }

    public class GroupEpisode
    {
        public DateTimeOffset air_date { get; set; }
        public int id { get; set; }
        public string name { get; set; }
        public string overview { get; set; }
        public int episode_number { get; set; }
        public int season_number { get; set; }
        public int order { get; set; }
    }
    
    public class CompactEpisodeGroupResponse
    {
        public string description { get; set; }
        public List<CompactEpisodeGroup> groups { get; set; }
        public string id { get; set; }
    }

    public class CompactEpisodeGroup
    {
        public string name { get; set; }
        public int order { get; set; }
        public List<CompactGroupEpisode> episodes { get; set; }
    }

    public class CompactGroupEpisode
    {
        public int episode_number { get; set; }
        public int season_number { get; set; }
        public int order { get; set; }
    }
}
