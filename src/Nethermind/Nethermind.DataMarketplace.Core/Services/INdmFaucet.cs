using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.DataMarketplace.Core.Services
{
    public interface INdmFaucet
    {
        Task<bool> TryRequestEthAsync(string host, Address address, UInt256 value);
    }
}