using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Core.Repositories
{
    public interface IEthRequestRepository
    {
        Task<EthRequest> GetLatestAsync(string host);
        Task AddAsync(EthRequest request);
        Task UpdateAsync(EthRequest request);
    }
}