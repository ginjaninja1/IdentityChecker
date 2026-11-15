using System;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using MediaBrowser.Model.Serialization;
using IdentityHelper.Configuration;
using IdentityHelper.UIBaseClasses.Views;
using IdentityHelper.UI.Config;

namespace IdentityHelper.UI
{
    internal class ConfigPageView : PluginPageView
    {
        private readonly IJsonSerializer jsonSerializer;
        private readonly ILogger logger;

        public ConfigPageView(PluginInfo pluginInfo, IServerApplicationHost applicationHost, ILogger logger)
            : base(pluginInfo.Id)
        {
            this.logger = logger;
            this.jsonSerializer = applicationHost.Resolve<IJsonSerializer>();
            this.ShowSave = false;
            this.ShowBack = true;
            this.AllowBack = true;
            Rebuild();
        }

        public override Task<IPluginUIView> OnSaveCommand(string itemId, string commandId, string data) => RunCommand(itemId, commandId, data);

        public override Task<IPluginUIView> RunCommand(string itemId, string commandId, string data)
        {
            if (!string.IsNullOrEmpty(data) && (commandId == "updateconfig" || commandId == "saveconfig"))
            {
                Save(data);
                return Task.FromResult<IPluginUIView>(this);
            }
            return Task.FromResult<IPluginUIView>(this);
        }

        private void Save(string data)
        {
            try
            {
                var incoming = this.jsonSerializer.DeserializeFromString<ConfigUI>(data);
                if (incoming == null) return;
                var config = Plugin.Instance.Configuration;
                config.EnablePlugin = incoming.EnablePlugin;
                config.ManualSource = Enum.IsDefined(typeof(ManualSeriesSearchSource), incoming.ManualSource)
                    ? incoming.ManualSource : ManualSeriesSearchSource.Both;
                config.AutomaticSource = Enum.IsDefined(typeof(AutomaticSeriesSearchSource), incoming.AutomaticSource)
                    ? incoming.AutomaticSource : AutomaticSeriesSearchSource.Inherit;
                config.ManualPreferOnTie = Enum.IsDefined(typeof(SeriesSearchTieBreakPreference), incoming.ManualPreferOnTie)
                    ? incoming.ManualPreferOnTie : SeriesSearchTieBreakPreference.TheTVDB;
                config.ApplySelectedIds = incoming.ApplySelectedIds;
                config.TitleWeight = ValidWeight(incoming.TitleWeight, 0.8);
                config.YearWeight = ValidWeight(incoming.YearWeight, 0.2);
                if (config.TitleWeight + config.YearWeight <= 0) { config.TitleWeight = 0.8; config.YearWeight = 0.2; }
                Plugin.Instance.SaveConfiguration();
                this.logger.Info("Identity Helper configuration saved");
            }
            catch (Exception ex) { this.logger.ErrorException("Error saving Identity Helper configuration", ex); }
            Rebuild();
        }

        private void Rebuild()
        {
            var config = Plugin.Instance.Configuration;
            this.ContentData = new ConfigUI {
                EnablePlugin = config.EnablePlugin, ManualSource = config.ManualSource,
                AutomaticSource = config.AutomaticSource, ManualPreferOnTie = config.ManualPreferOnTie,
                ApplySelectedIds = config.ApplySelectedIds,
                TitleWeight = config.TitleWeight, YearWeight = config.YearWeight
            };
        }

        private static double ValidWeight(double value, double fallback) => double.IsNaN(value) || double.IsInfinity(value) || value < 0 ? fallback : value;
    }
}

