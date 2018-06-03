using Nethermind.Blockchain;
using Nethermind.Discovery.Lifecycle;
using Nethermind.Discovery.RoutingTable;
using Nethermind.Discovery.Stats;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth;

namespace Nethermind.Discovery
{
    public class Peer
    {
        public Peer(Node node, INodeStats nodeStats)
        {
            Node = node;
            NodeStats = nodeStats;
        }

        public Peer(INodeLifecycleManager manager)
        {
            Node = manager.ManagedNode;
            NodeLifecycleManager = manager;
            NodeStats = manager.NodeStats;
        }

        public Node Node { get; }
        public INodeLifecycleManager NodeLifecycleManager { get; set; }
        public INodeStats NodeStats { get; }
        public IP2PSession Session { get; set; }
        public ISynchronizationPeer SynchronizationPeer { get; set; }
    }
}