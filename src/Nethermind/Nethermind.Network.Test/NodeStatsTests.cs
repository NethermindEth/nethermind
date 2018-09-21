using System.Linq;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [TestFixture]
    public class NodeStatsTests
    {
        private INodeStats _nodeStats;

        [SetUp]
        public void Initialize()
        {
            var nodeFactory = new NodeFactory();
            var node = nodeFactory.CreateNode("192.1.1.1", 3333);
            StatsConfig config = new StatsConfig();
            config.CaptureNodeLatencyStatsEventHistory = true;
            _nodeStats = new NodeStats(node, config, NullLogManager.Instance);
        }

        [Test]
        public void LatencyCaptureTest()
        {
            _nodeStats.AddLatencyCaptureEvent(NodeLatencyStatType.BlockHeaders, 30);
            _nodeStats.AddLatencyCaptureEvent(NodeLatencyStatType.BlockHeaders, 51);
            _nodeStats.AddLatencyCaptureEvent(NodeLatencyStatType.BlockHeaders, 140);
            _nodeStats.AddLatencyCaptureEvent(NodeLatencyStatType.BlockHeaders, 110);
            _nodeStats.AddLatencyCaptureEvent(NodeLatencyStatType.BlockHeaders, 133);
            _nodeStats.AddLatencyCaptureEvent(NodeLatencyStatType.BlockHeaders, 51);
            _nodeStats.AddLatencyCaptureEvent(NodeLatencyStatType.BlockHeaders, 140);
            _nodeStats.AddLatencyCaptureEvent(NodeLatencyStatType.BlockHeaders, 110);
            _nodeStats.AddLatencyCaptureEvent(NodeLatencyStatType.BlockHeaders, 133);
            _nodeStats.AddLatencyCaptureEvent(NodeLatencyStatType.BlockHeaders, 51);
            _nodeStats.AddLatencyCaptureEvent(NodeLatencyStatType.BlockHeaders, 140);
            _nodeStats.AddLatencyCaptureEvent(NodeLatencyStatType.BlockHeaders, 110);
            _nodeStats.AddLatencyCaptureEvent(NodeLatencyStatType.BlockHeaders, 133);

            var av = _nodeStats.GetAverageLatency(NodeLatencyStatType.BlockHeaders);
            var events = _nodeStats.LatencyHistory.ToList();
            var avCompare = events.Sum(x => x.Latency) / events.Count();
            Assert.AreEqual(av, avCompare);
        }
    }
}