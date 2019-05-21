/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Threading.Tasks;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [TestFixture]
    public class NodeStatsTests
    {
        private INodeStats _nodeStats;
        private Node _node;
        private StatsConfig _config;

        [SetUp]
        public void Initialize()
        {
            _node = new Node("192.1.1.1", 3333);
            _config = new StatsConfig();
            _config.CaptureNodeLatencyStatsEventHistory = true;
        }

        [Test]
        public void LatencyCaptureTest()
        {
            _nodeStats = new NodeStatsLight(_node, _config);
            
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
            Assert.AreEqual(102, av);
        }

        [Test]
        public void DisconnectDelayTest()
        {
            _nodeStats = new NodeStatsLight(_node, _config);
            
            var isConnDelayed = _nodeStats.IsConnectionDelayed();
            Assert.IsFalse(isConnDelayed.Result, "before disconnect");
            
            _nodeStats.AddNodeStatsDisconnectEvent(DisconnectType.Remote, DisconnectReason.Other);
            isConnDelayed = _nodeStats.IsConnectionDelayed();
            Assert.IsTrue(isConnDelayed.Result, "just after disconnect");
            Assert.AreEqual(NodeStatsEventType.Disconnect, isConnDelayed.DelayReason);
            var task = Task.Delay(125);
            task.Wait();
            isConnDelayed = _nodeStats.IsConnectionDelayed();
            Assert.IsFalse(isConnDelayed.Result, "125ms after disconnect");
        }
        
        [Test]
        public void FailedConnectionDelayTest()
        {
            _nodeStats = new NodeStatsLight(_node, _config);
            
            var isConnDelayed = _nodeStats.IsConnectionDelayed();
            Assert.IsFalse(isConnDelayed.Result, "before failure");
            
            _nodeStats.AddNodeStatsEvent(NodeStatsEventType.ConnectionFailed);
            isConnDelayed = _nodeStats.IsConnectionDelayed();
            Assert.IsTrue(isConnDelayed.Result, "just after failure");
            Assert.AreEqual(NodeStatsEventType.ConnectionFailed, isConnDelayed.DelayReason);
            var task = Task.Delay(125);
            task.Wait();
            isConnDelayed = _nodeStats.IsConnectionDelayed();
            Assert.IsFalse(isConnDelayed.Result, "125ms after failure");
        }
    }
}