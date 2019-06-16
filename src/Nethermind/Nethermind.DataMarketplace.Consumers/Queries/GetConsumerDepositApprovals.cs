using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Queries
{
    public class GetConsumerDepositApprovals : PagedQueryBase
    {
        public Keccak DataHeaderId { get; set; }
        public Address Provider { get; set; }
        public bool OnlyPending { get; set; }
    }
}