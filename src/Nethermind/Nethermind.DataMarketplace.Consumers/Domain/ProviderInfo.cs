using Nethermind.Core;

namespace Nethermind.DataMarketplace.Consumers.Domain
{
    public class ProviderInfo
    {
        public string Name { get; }
        public Address Address { get; }

        public ProviderInfo(string name, Address address)
        {
            Name = name;
            Address = address;
        }
    }
}