using MediaBrowser.Controller;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Plugins.UI.Views;
using IdentityChecker.UIBaseClasses;
using System.Threading.Tasks;

namespace IdentityChecker.UI
{
    internal class MainPageController : ControllerBase
    {
        private readonly PluginInfo pluginInfo;
        private readonly IServerApplicationHost applicationHost;
        private readonly ILogger logger;



        public MainPageController(
            PluginInfo pluginInfo,
            IServerApplicationHost applicationHost,
            ILogger logger)
            : base(pluginInfo.Id)
        {
            this.pluginInfo = pluginInfo;
            this.applicationHost = applicationHost;
            this.logger = logger;

            this.PageInfo = new PluginPageInfo
            {
                Name = "IdentityChecker",
                EnableInMainMenu = false,
                DisplayName = "Series Identity Resolver",
                MenuIcon = "search",
                IsMainConfigPage = true
            };
        }


        public override PluginPageInfo PageInfo { get; }


        public override Task<IPluginUIView> CreateDefaultPageView()
        {
            IPluginUIView view =
                new ConfigPageView(
                    this.pluginInfo,
                    this.applicationHost,
                    this.logger);

            return Task.FromResult(view);
        }

        public System.Collections.Generic.IReadOnlyList<IPluginUIPageController> TabPageControllers =>
            System.Array.Empty<IPluginUIPageController>();


    }
}
