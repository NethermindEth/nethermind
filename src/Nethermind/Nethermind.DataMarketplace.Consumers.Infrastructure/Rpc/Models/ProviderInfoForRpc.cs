using Nethermind.Core;
using Nethermind.DataMarketplace.Consumers.Domain;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rpc.Models
{
    public class ProviderInfoForRpc
    {
        public string Name { get; set; }
        public Address Address { get; set; }


        public ProviderInfoForRpc()
        {
        }

        public ProviderInfoForRpc(ProviderInfo provider)
        {
            Name = provider.Name;
            Address = provider.Address;
        }
    }
}