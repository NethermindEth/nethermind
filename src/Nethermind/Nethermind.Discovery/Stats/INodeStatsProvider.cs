using Nethermind.Core.Crypto;

namespace Nethermind.Discovery.Stats
{
    public interface INodeStatsProvider
    {
        INodeStats GetNodeStats(PublicKey nodeId);
    }
}