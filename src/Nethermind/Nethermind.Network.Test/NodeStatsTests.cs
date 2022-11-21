//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NUnit.Framework;

namespace Nethermind.Network.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class NodeStatsTests
    {
        private INodeStats _nodeStats;
        private Node _node;

        [SetUp]
        public void Initialize()
        {
            _node = new Node(TestItem.PublicKeyA, "192.1.1.1", 3333);
        }

        [TestCase(TransferSpeedType.Bodies)]
        [TestCase(TransferSpeedType.Headers)]
        [TestCase(TransferSpeedType.Receipts)]
        [TestCase(TransferSpeedType.Latency)]
        [TestCase(TransferSpeedType.NodeData)]
        public void TransferSpeedCaptureTest(TransferSpeedType speedType)
        {
            _nodeStats = new NodeStatsLight(_node, 0.5m);

            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 30);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 51);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 140);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 110);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 133);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 51);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 140);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 110);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 133);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 51);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 140);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 110);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 133);

            var av = _nodeStats.GetAverageTransferSpeed(speedType);
            Assert.AreEqual(122, av);

            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 0);
            _nodeStats.AddTransferSpeedCaptureEvent(speedType, 0);

            av = _nodeStats.GetAverageTransferSpeed(speedType);
            Assert.AreEqual(30, av);
        }

        [Test]
        public async Task DisconnectDelayTest()
        {
            _nodeStats = new NodeStatsLight(_node);

            var isConnDelayed = _nodeStats.IsConnectionDelayed();
            Assert.IsFalse(isConnDelayed.Result, "before disconnect");

            _nodeStats.AddNodeStatsDisconnectEvent(DisconnectType.Remote, DisconnectReason.Other);
            isConnDelayed = _nodeStats.IsConnectionDelayed();
            Assert.IsTrue(isConnDelayed.Result, "just after disconnect");
            Assert.AreEqual(NodeStatsEventType.Disconnect, isConnDelayed.DelayReason);
            await Task.Delay(125);
            isConnDelayed = _nodeStats.IsConnectionDelayed();
            Assert.IsFalse(isConnDelayed.Result, "125ms after disconnect");
        }

        [TestCase(NodeStatsEventType.Connecting, true)]
        [TestCase(NodeStatsEventType.None, false)]
        [TestCase(NodeStatsEventType.ConnectionFailedTargetUnreachable, true)]
        [TestCase(NodeStatsEventType.ConnectionFailed, true)]
        public void DisconnectDelayDueToNodeStatsEvent(NodeStatsEventType eventType, bool connectionDelayed)
        {
            _nodeStats = new NodeStatsLight(_node);

            (bool isConnDelayed, NodeStatsEventType? _) = _nodeStats.IsConnectionDelayed();
            Assert.IsFalse(isConnDelayed, "before disconnect");

            _nodeStats.AddNodeStatsEvent(eventType);
            (isConnDelayed, _) = _nodeStats.IsConnectionDelayed();
            isConnDelayed.Should().Be(connectionDelayed);
        }

        [TestCase(DisconnectType.Local, DisconnectReason.Breach1, false)]
        [TestCase(DisconnectType.Local, DisconnectReason.UselessPeer, true)]
        [TestCase(DisconnectType.Remote, DisconnectReason.ClientQuitting, true)]
        public async Task DisconnectDelayDueToDisconnect(DisconnectType disconnectType, DisconnectReason reason, bool connectionDelayed)
        {
            _nodeStats = new NodeStatsLight(_node);

            (bool isConnDelayed, NodeStatsEventType? _) = _nodeStats.IsConnectionDelayed();
            Assert.IsFalse(isConnDelayed, "before disconnect");

            _nodeStats.AddNodeStatsDisconnectEvent(disconnectType, reason);
            await Task.Delay(125); // Standard disconnect delay without specific handling
            (isConnDelayed, _) = _nodeStats.IsConnectionDelayed();
            isConnDelayed.Should().Be(connectionDelayed);
        }
    }
}
