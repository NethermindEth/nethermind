using System.Linq;
using System.Threading.Tasks;
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
        private StatsConfig _config;

        [SetUp]
        public void Initialize()
        {
            var nodeFactory = new NodeFactory();
            var node = nodeFactory.CreateNode("192.1.1.1", 3333);
            _config = new StatsConfig();
            _config.CaptureNodeLatencyStatsEventHistory = true;
            _nodeStats = new NodeStats(node, _config, NullLogManager.Instance);
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

        [Test]
        public void DisconnectDelayTest()
        {
            var isConnDelayed = _nodeStats.IsConnectionDelayed();
            Assert.IsFalse(isConnDelayed.Result);
            
            _nodeStats.AddNodeStatsDisconnectEvent(DisconnectType.Remote, DisconnectReason.Other);
            isConnDelayed = _nodeStats.IsConnectionDelayed();
            Assert.IsTrue(isConnDelayed.Result);
            Assert.AreEqual(NodeStatsEventType.Disconnect, isConnDelayed.DelayReason);
            var task = Task.Delay(100);
            task.Wait();
            isConnDelayed = _nodeStats.IsConnectionDelayed();
            Assert.IsFalse(isConnDelayed.Result);
        }
        
        [Test]
        public void FailedConnectionDelayTest()
        {
            var isConnDelayed = _nodeStats.IsConnectionDelayed();
            Assert.IsFalse(isConnDelayed.Result);
            
            _nodeStats.AddNodeStatsEvent(NodeStatsEventType.ConnectionFailed);
            isConnDelayed = _nodeStats.IsConnectionDelayed();
            Assert.IsTrue(isConnDelayed.Result);
            Assert.AreEqual(NodeStatsEventType.ConnectionFailed, isConnDelayed.DelayReason);
            var task = Task.Delay(100);
            task.Wait();
            isConnDelayed = _nodeStats.IsConnectionDelayed();
            Assert.IsFalse(isConnDelayed.Result);
        }
    }
}