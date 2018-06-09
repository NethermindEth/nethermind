using System.Collections.Concurrent;
using Nethermind.Core.Crypto;
using Nethermind.Network.Discovery;

namespace Nethermind.Network.Stats
{
    public class NodeStatsProvider : INodeStatsProvider
    {
        private readonly IDiscoveryConfigurationProvider _discoveryConfigurationProvider;

        private readonly ConcurrentDictionary<PublicKey, INodeStats> _nodeStats = new ConcurrentDictionary<PublicKey, INodeStats>();

        public NodeStatsProvider(IDiscoveryConfigurationProvider discoveryConfigurationProvider)
        {
            _discoveryConfigurationProvider = discoveryConfigurationProvider;
        }

        public INodeStats GetNodeStats(PublicKey nodeId)
        {
            return _nodeStats.GetOrAdd(nodeId, x => new NodeStats(_discoveryConfigurationProvider));
        }
    }
}