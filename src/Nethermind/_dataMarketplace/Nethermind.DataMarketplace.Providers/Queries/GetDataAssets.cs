using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Providers.Queries
{
    public class GetDataAssets : PagedQueryBase
    {
        public bool OnlyPublishable { get; set; }
    }
}