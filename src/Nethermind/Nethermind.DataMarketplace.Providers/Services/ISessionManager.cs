using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.DataMarketplace.Providers.Peers;

namespace Nethermind.DataMarketplace.Providers.Services
{
    public interface ISessionManager
    {
        int GetNodesCount(Keccak depositId);
        ProviderSession? GetSession(Keccak depositId, INdmProviderPeer peer);
        IEnumerable<ConsumerNode> GetConsumerNodes(Keccak? depositId = null);
        ConsumerNode? GetConsumerNode(INdmProviderPeer peer);
        void SetSession(ProviderSession session, INdmProviderPeer peer);
        void AddPeer(INdmProviderPeer peer);
        Task FinishSessionsAsync(INdmProviderPeer peer, bool removePeer = true);
        Task FinishSessionAsync(Keccak depositId, INdmProviderPeer peer, bool removePeer = true);
    }
}