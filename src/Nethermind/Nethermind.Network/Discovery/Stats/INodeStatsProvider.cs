using Nethermind.Core.Crypto;

namespace Nethermind.Network.Discovery.Stats
{
    public interface INodeStatsProvider
    {
        INodeStats GetNodeStats(PublicKey nodeId);
    }
}