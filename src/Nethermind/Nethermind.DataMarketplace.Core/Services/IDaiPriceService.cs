using System.Threading.Tasks;

namespace Nethermind.DataMarketplace.Core.Services
{
    public interface IDaiPriceService
    {
        Task UpdateAsync();
        decimal UsdPrice { get; }
        ulong UpdatedAt { get; }
    }
}
