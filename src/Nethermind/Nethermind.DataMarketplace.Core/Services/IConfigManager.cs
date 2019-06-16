using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Configs;

namespace Nethermind.DataMarketplace.Core.Services
{
    public interface IConfigManager
    {
        Task<NdmConfig> GetAsync(string id);
        Task UpdateAsync(NdmConfig config);
    }
}