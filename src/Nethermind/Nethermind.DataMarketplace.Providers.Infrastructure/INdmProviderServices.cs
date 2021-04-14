using Nethermind.DataMarketplace.Providers.Services;

namespace Nethermind.DataMarketplace.Providers.Infrastructure
{
    public interface INdmProviderServices
    {
        IProviderService ProviderService { get; }
    }
}