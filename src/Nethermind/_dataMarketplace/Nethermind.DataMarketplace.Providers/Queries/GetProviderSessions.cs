using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Providers.Queries
{
    public class GetProviderSessions : PagedQueryBase
    {
        public Keccak? DepositId { get; set; }
        public Keccak? DataAssetId { get; set; }
        public PublicKey? ConsumerNodeId { get; set; }
        public Address? ConsumerAddress { get; set; }
        public PublicKey? ProviderNodeId { get; set; }
        public Address? ProviderAddress { get; set; }
    }
}