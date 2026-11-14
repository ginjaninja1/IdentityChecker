using MediaBrowser.Model.Plugins;

namespace IdentityChecker.Configuration
{
    public enum SeriesSearchSourceMode
    {
        Tvdb,
        Tmdb,
        Both
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public bool EnablePlugin { get; set; } = true;
        public SeriesSearchSourceMode SourceMode { get; set; } = SeriesSearchSourceMode.Tvdb;
        public bool ApplySelectedIds { get; set; } = false;
        public double TitleWeight { get; set; } = 0.8;
        public double YearWeight { get; set; } = 0.2;
        public double MinimumScore { get; set; } = 0.72;
        public double MinimumScoreMargin { get; set; } = 0.08;
        public bool VerboseCandidateLogging { get; set; } = true;
    }
}
