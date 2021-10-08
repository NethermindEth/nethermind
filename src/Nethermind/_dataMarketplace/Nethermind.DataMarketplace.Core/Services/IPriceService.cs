using System.Threading.Tasks;

namespace Nethermind.DataMarketplace.Core.Services
{
    public interface IPriceService
    {
        Task UpdateAsync(params string[] currencies);
        PriceInfo? Get(string currency);
    }

    public class PriceInfo
    {
        public decimal UsdPrice { get; }
        public ulong UpdatedAt { get; }

        public PriceInfo(decimal usdPrice, ulong updatedAt)
        {
            UsdPrice = usdPrice;
            UpdatedAt = updatedAt;
        }
    }
}
