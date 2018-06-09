using Nethermind.Core.Crypto;

namespace Nethermind.Network.Stats
{
    public interface INodeStatsProvider
    {
        INodeStats GetNodeStats(PublicKey nodeId);
    }
}