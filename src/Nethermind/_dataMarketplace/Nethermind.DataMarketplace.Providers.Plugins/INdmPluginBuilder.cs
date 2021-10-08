namespace Nethermind.DataMarketplace.Providers.Plugins
{
    public interface INdmPluginBuilder
    {
        INdmPlugin? Build(string description);
    }
}