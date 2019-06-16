using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.DataMarketplace.Consumers.Domain;

namespace Nethermind.DataMarketplace.Consumers.Repositories
{
    public interface IProviderRepository
    {
        Task<IReadOnlyList<DataHeaderInfo>> GetDataHeadersAsync();
        Task<IReadOnlyList<ProviderInfo>> GetProvidersAsync();
    }
}