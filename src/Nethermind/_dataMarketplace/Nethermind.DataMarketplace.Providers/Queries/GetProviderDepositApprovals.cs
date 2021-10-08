using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Providers.Queries
{
    public class GetProviderDepositApprovals : PagedQueryBase
    {
        public Keccak? DataAssetId { get; set; }
        public Address? Consumer { get; set; }
        public bool OnlyPending { get; set; }
    }
}