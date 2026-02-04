using MediaBrowser.Controller.Entities;
using System;
using static StrmAssistant.Options.IntroSkipOptions;
using static StrmAssistant.Options.Utility;

namespace StrmAssistant.IntroSkip
{
    public class PlaySessionData
    {
        public PlaySessionData(BaseItem item)
        {
            IntroStart = Plugin.ChapterApi.GetIntroStart(item);
            IntroEnd = Plugin.ChapterApi.GetIntroEnd(item);
            CreditsStart = Plugin.ChapterApi.GetCreditsStart(item);
        }

        public long? IntroStart { get; set; }

        public long? IntroEnd { get; set; }

        public long? CreditsStart { get; set; }

        public long PlaybackStartTicks { get; set; } = 0;

        public long PreviousPositionTicks { get; set; } = 0;

        public DateTime PreviousEventTime { get; set; } = DateTime.MinValue;

        public long? FirstJumpPositionTicks { get; set; } = null;

        public long? LastJumpPositionTicks { get; set; } = null;

        public long MaxIntroDurationTicks { get; set; } =
            Plugin.Instance.IntroSkipStore.GetOptions().MaxIntroDurationSeconds * TimeSpan.TicksPerSecond;

        public long MaxCreditsDurationTicks { get; set; } =
            Plugin.Instance.IntroSkipStore.GetOptions().MaxCreditsDurationSeconds * TimeSpan.TicksPerSecond;

        public long MinOpeningPlotDurationTicks { get; set; } =
            Plugin.Instance.IntroSkipStore.GetOptions().MinOpeningPlotDurationSeconds * TimeSpan.TicksPerSecond;

        public DateTime? LastPauseEventTime { get; set; } = null;

        public DateTime? LastPlaybackRateChangeEventTime { get; set; } = null;

        public bool NoDetectionButReset { get; set; } =
            IsIntroSkipPreferenceSelected(IntroSkipPreference.NoDetectionButReset);
    }
}
