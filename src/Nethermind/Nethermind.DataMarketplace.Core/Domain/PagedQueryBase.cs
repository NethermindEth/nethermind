namespace Nethermind.DataMarketplace.Core.Domain
{
    public class PagedQueryBase
    {
        public int Page { get; set; } = 1;
        public int Results { get; set; } = 10;
    }
}