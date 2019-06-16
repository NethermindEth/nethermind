using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Queries
{
    public class GetDeposits : PagedQueryBase
    {
        public bool OnlyUnverified { get; set; }
    }
}