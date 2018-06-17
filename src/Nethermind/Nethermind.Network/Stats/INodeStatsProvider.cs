using Nethermind.Core.Crypto;
using Nethermind.Core.Model;

namespace Nethermind.Network.Stats
{
    public interface INodeStatsProvider
    {
        INodeStats GetNodeStats(NodeId nodeId);
    }
}