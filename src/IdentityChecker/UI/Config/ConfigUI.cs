using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Elements;
using MediaBrowser.Model.Attributes;
using System.ComponentModel;
using IdentityChecker.Configuration;

namespace IdentityChecker.UI.Config
{
    public class ConfigUI : EditableOptionsBase
    {
        public override string EditorTitle => "Series Identity Resolver";
        public override string EditorDescription => "Rank TVDB and TMDB series matches before Emby's standard metadata providers run.";

        public CaptionItem GeneralHeading { get; set; } = new CaptionItem("Resolver");

        [DisplayName("Enable resolver")]
        [AutoPostBack("updateconfig", nameof(EnablePlugin))]
        public bool EnablePlugin { get; set; } = true;

        [DisplayName("Search source")]
        [AutoPostBack("updateconfig", nameof(SourceMode))]
        public SeriesSearchSourceMode SourceMode { get; set; } = SeriesSearchSourceMode.Inherit;

        [DisplayName("Apply selected IDs")]
        [Description("Off by default (dry run). Turn on to seed a confident TVDB/TMDB ID for Emby's stock metadata providers.")]
        [AutoPostBack("updateconfig", nameof(ApplySelectedIds))]
        public bool ApplySelectedIds { get; set; }

        public CaptionItem ScoringHeading { get; set; } = new CaptionItem("Match confidence");
        [DisplayName("Title weight")]
        [AutoPostBack("updateconfig", nameof(TitleWeight))]
        public double TitleWeight { get; set; } = 0.8;

        [DisplayName("Year weight")]
        [AutoPostBack("updateconfig", nameof(YearWeight))]
        public double YearWeight { get; set; } = 0.2;

        [DisplayName("Verbose candidate logging")]
        [AutoPostBack("updateconfig", nameof(VerboseCandidateLogging))]
        public bool VerboseCandidateLogging { get; set; } = true;

        [Browsable(false)] public string Guidance => "Place this provider ahead of the selected stock provider to seed IDs during refresh. If placed after it, it is diagnostic only.";
    }
}
