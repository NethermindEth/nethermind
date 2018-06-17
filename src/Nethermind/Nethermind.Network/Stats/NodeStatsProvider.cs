using System.Collections.Concurrent;
using Nethermind.Core.Crypto;
using Nethermind.Core.Model;
using Nethermind.Network.Discovery;

namespace Nethermind.Network.Stats
{
    public class NodeStatsProvider : INodeStatsProvider
    {
        private readonly IDiscoveryConfigurationProvider _discoveryConfigurationProvider;

        private readonly ConcurrentDictionary<NodeId, INodeStats> _nodeStats = new ConcurrentDictionary<NodeId, INodeStats>();

        public NodeStatsProvider(IDiscoveryConfigurationProvider discoveryConfigurationProvider)
        {
            _discoveryConfigurationProvider = discoveryConfigurationProvider;
        }

        public INodeStats GetNodeStats(NodeId nodeId)
        {
            return _nodeStats.GetOrAdd(nodeId, x => new NodeStats(_discoveryConfigurationProvider));
        }
    }
}