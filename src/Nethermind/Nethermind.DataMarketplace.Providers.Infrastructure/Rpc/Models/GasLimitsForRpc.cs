using Nethermind.DataMarketplace.Providers.Domain;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Rpc.Models
{
    public class GasLimitsForRpc
    {
        public ulong Payment { get; set; }

        public GasLimitsForRpc()
        {
        }

        public GasLimitsForRpc(GasLimits gasLimits)
        {
            Payment = gasLimits.Payment;
        }
    }
}