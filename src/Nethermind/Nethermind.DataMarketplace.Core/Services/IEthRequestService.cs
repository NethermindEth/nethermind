using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.DataMarketplace.Core.Services
{
    public interface IEthRequestService
    {
        string FaucetHost { get; }
        void UpdateFaucet(INdmPeer peer);
        Task<bool> TryRequestEthAsync(Address address, UInt256 value);
    }
}