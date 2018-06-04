using Nethermind.Core;
using Nethermind.Discovery.RoutingTable;

namespace Nethermind.Discovery
{
    public interface IPeerStorage
    {
        (Node Node, long PersistedReputation)[] GetPersistedPeers();
        void UpdatePeers(Peer[] peers);
        void RemovePeers(Peer[] nodes);
        void StartBatch();
        void Commit();
        bool AnyPendingChange();
    }
}