using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Configs;

namespace Nethermind.DataMarketplace.Core.Repositories
{
    public interface IConfigRepository
    {
        Task<NdmConfig> GetAsync(string id);
        Task AddAsync(NdmConfig config);
        Task UpdateAsync(NdmConfig config);
    }
}