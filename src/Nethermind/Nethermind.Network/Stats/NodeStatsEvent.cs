using System;
using Nethermind.Network.Rlpx;

namespace Nethermind.Network.Stats
{
    public class NodeStatsEvent
    {
        public NodeStatsEventType EventType { get; set; }
        public DateTime EventDate { get; set; }
        public P2PNodeDetails P2PNodeDetails { get; set; }
        public Eth62NodeDetails Eth62NodeDetails { get; set; }
        public DisconnectDetails DisconnectDetails { get; set; }
        public ConnectionDirection? ConnectionDirection { get; set; }
        public SyncNodeDetails SyncNodeDetails { get; set; }
    }
}