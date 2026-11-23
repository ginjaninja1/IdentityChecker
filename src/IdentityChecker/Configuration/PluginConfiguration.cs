using MediaBrowser.Model.Plugins;

namespace IdentityHelper.Configuration
{
    public enum ManualSeriesSearchSource
    {
        TheTVDB,
        TheMovieDb,
        Both
    }

    public enum AutomaticSeriesSearchSource
    {
        Inherit,
        TheTVDB,
        TheMovieDb,
        Both
    }

    public enum SeriesSearchTieBreakPreference
    {
        TheTVDB,
        TheMovieDb
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        public bool EnablePlugin { get; set; } = true;
        public ManualSeriesSearchSource ManualSource { get; set; } = ManualSeriesSearchSource.Both;
        public AutomaticSeriesSearchSource AutomaticSource { get; set; } = AutomaticSeriesSearchSource.Inherit;
        public SeriesSearchTieBreakPreference ManualPreferOnTie { get; set; } = SeriesSearchTieBreakPreference.TheTVDB;
        public double TitleWeight { get; set; } = 0.8;
        public double YearWeight { get; set; } = 0.2;
    }
}
