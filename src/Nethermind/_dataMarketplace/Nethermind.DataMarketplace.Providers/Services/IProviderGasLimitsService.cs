using Nethermind.DataMarketplace.Providers.Domain;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public interface IProviderGasLimitsService
    {
        GasLimits GasLimits { get; }
    }
}