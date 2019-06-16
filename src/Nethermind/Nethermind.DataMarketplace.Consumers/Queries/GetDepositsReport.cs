using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Queries
{
    public class GetDepositsReport : PagedQueryBase
    {
        public Keccak DepositId { get; set; }
        public Keccak HeaderId { get; set; }
        public Address Provider { get; set; }
    }
}