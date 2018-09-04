using System;
using Nethermind.Stats;

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

    public class NodeLatencyStatsEvent
    {
        public NodeLatencyStatType StatType { get; set; }
        public DateTime CaptureTime { get; set; }
        public long Latency { get; set; }
    }
}