using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Providers.Queries
{
    public class GetPaymentClaimsReport : PagedQueryBase
    {
        public Keccak? DepositId { get; set; }
        public Keccak? AssetId { get; set; }
        public Address? Consumer { get; set; }
    }
}