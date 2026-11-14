
using MediaBrowser.Common;
using MediaBrowser.Model.Logging;
using IdentityChecker.UI.Config;
using IdentityChecker.UIBaseClasses.Store;

namespace IdentityChecker.Storage
{
    public class BasicsOptionsStore : SimpleFileStore<ConfigUI>
    {
        public BasicsOptionsStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
        : base(applicationHost, logger, pluginFullName)
        {
        }
    }
}
