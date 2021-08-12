using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Providers.Peers;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public interface IDepositManager
    {
        Task<IDepositNodesHandler> InitAsync(Keccak depositId, uint unpaidSessionUnits = 0);
        Task HandleConsumedUnitAsync(Keccak depositId);
        Task HandleUnpaidUnitsAsync(Keccak depositId, INdmProviderPeer peer);
        uint GetConsumedUnits(Keccak depositId);
        bool HasAvailableUnits(Keccak depositId);
        bool TryIncreaseSentUnits(Keccak depositId);
        void ChangeAddress(Address address);
        void ChangeColdWalletAddress(Address address);
        bool IsExpired(Keccak depositId);
        uint GetUnclaimedUnits(Keccak depositId);
    }
}