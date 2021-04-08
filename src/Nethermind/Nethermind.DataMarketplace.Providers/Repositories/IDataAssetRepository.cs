using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Providers.Queries;

namespace Nethermind.DataMarketplace.Providers.Repositories
{
    public interface IDataAssetRepository
    {
        Task<bool> ExistsAsync(Keccak id);
        Task<DataAsset?> GetAsync(Keccak id);
        Task<PagedResult<DataAsset>> BrowseAsync(GetDataAssets query);
        Task AddAsync(DataAsset dataAsset);
        Task UpdateAsync(DataAsset dataAsset);
        Task RemoveAsync(Keccak id);
    }
}