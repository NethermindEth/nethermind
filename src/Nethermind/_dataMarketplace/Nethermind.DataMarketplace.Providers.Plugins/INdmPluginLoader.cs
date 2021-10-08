using System.Collections.Generic;

namespace Nethermind.DataMarketplace.Providers.Plugins
{
    public interface INdmPluginLoader
    {
        IEnumerable<INdmPlugin> Load();
    }
}