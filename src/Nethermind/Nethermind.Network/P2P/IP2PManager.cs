using System.Collections.Generic;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.P2P
{
    public interface IP2PManager
    {
        IReadOnlyCollection<RlpxPeer> ActivePeers { get; }
    }
}