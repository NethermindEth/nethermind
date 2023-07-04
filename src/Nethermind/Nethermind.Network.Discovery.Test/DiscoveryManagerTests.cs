// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Network.Enr;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Discovery.Test
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class DiscoveryManagerTests
    {
        private const string TestPrivateKeyHex = "0x3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266";

        private readonly INetworkConfig _networkConfig = new NetworkConfig();
        private IDiscoveryManager _discoveryManager = null!;
        private IMsgSender _msgSender = null!;
        private INodeTable _nodeTable = null!;
        private const int Port = 1;
        private const string Host = "192.168.1.17";
        private Node[] _nodes = null!;
        private PublicKey _publicKey = null!;

        [SetUp]
        public void Initialize()
        {
            NetworkNodeDecoder.Init();
            PrivateKey privateKey = new(TestPrivateKeyHex);
            _publicKey = privateKey.PublicKey;
            LimboLogs? logManager = LimboLogs.Instance;

            IDiscoveryConfig discoveryConfig = new DiscoveryConfig();
            discoveryConfig.PongTimeout = 100;

            _msgSender = Substitute.For<IMsgSender>();
            NodeDistanceCalculator calculator = new(discoveryConfig);

            _networkConfig.ExternalIp = "99.10.10.66";
            _networkConfig.LocalIp = "10.0.0.5";

            _nodeTable = new NodeTable(calculator, discoveryConfig, _networkConfig, logManager);
            _nodeTable.Initialize(TestItem.PublicKeyA);

            EvictionManager evictionManager = new(_nodeTable, logManager);
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            NodeLifecycleManagerFactory lifecycleFactory = new(_nodeTable, evictionManager,
                    new NodeStatsManager(timerFactory, logManager), new NodeRecord(), discoveryConfig, Timestamper.Default, logManager);

            _nodes = new[] { new Node(TestItem.PublicKeyA, "192.168.1.18", 1), new Node(TestItem.PublicKeyB, "192.168.1.19", 2) };

            IFullDb nodeDb = new SimpleFilePublicKeyDb("Test", "test_db", logManager);
            _discoveryManager = new DiscoveryManager(lifecycleFactory, _nodeTable, new NetworkStorage(nodeDb, logManager), discoveryConfig, logManager);
            _discoveryManager.MsgSender = _msgSender;
        }

        [Test, Retry(3)]
        public async Task OnPingMessageTest()
        {
            //receiving ping
            IPEndPoint address = new(IPAddress.Parse(Host), Port);
            _discoveryManager.OnIncomingMsg(new PingMsg(_publicKey, GetExpirationTime(), address, _nodeTable.MasterNode!.Address, new byte[32]) { FarAddress = address });
            await Task.Delay(500);

            // expecting to send pong
            _msgSender.Received(1).SendMsg(Arg.Is<PongMsg>(m => m.FarAddress!.Address.ToString() == Host && m.FarAddress.Port == Port));

            // send pings to  new node
            _msgSender.Received().SendMsg(Arg.Is<PingMsg>(m => m.FarAddress!.Address.ToString() == Host && m.FarAddress.Port == Port));
        }

        [Test, Ignore("Add bonding"), Retry(3)]
        public void OnPongMessageTest()
        {
            //receiving pong
            ReceiveSomePong();

            //expecting to activate node as valid peer
            IEnumerable<Node> nodes = _nodeTable.GetClosestNodes().ToArray();
            Assert.That(nodes.Count(), Is.EqualTo(1));
            Node node = nodes.First();
            Assert.That(node.Host, Is.EqualTo(Host));
            Assert.That(node.Port, Is.EqualTo(Port));
            INodeLifecycleManager? manager = _discoveryManager.GetNodeLifecycleManager(node);
            Assert.That(manager?.State, Is.EqualTo(NodeLifecycleState.Active));
        }

        [Test, Ignore("Add bonding"), Retry(3)]
        public void OnFindNodeMessageTest()
        {
            //receiving pong to have a node in the system
            ReceiveSomePong();

            //expecting to activate node as valid peer
            IEnumerable<Node> nodes = _nodeTable.GetClosestNodes().ToArray();
            Assert.That(nodes.Count(), Is.EqualTo(1));
            Node node = nodes.First();
            Assert.That(node.Host, Is.EqualTo(Host));
            Assert.That(node.Port, Is.EqualTo(Port));
            INodeLifecycleManager? manager = _discoveryManager.GetNodeLifecycleManager(node);
            Assert.That(manager?.State, Is.EqualTo(NodeLifecycleState.Active));

            //receiving findNode
            FindNodeMsg msg = new(_publicKey, GetExpirationTime(), Build.A.PrivateKey.TestObject.PublicKey.Bytes);
            msg.FarAddress = new IPEndPoint(IPAddress.Parse(Host), Port);
            _discoveryManager.OnIncomingMsg(msg);

            //expecting to respond with sending Neighbors
            _msgSender.Received(1).SendMsg(Arg.Is<NeighborsMsg>(m => m.FarAddress!.Address.ToString() == Host && m.FarAddress.Port == Port));
        }

        [Test, Retry(3)]
        public void MemoryTest()
        {
            //receiving pong to have a node in the system
            for (int a = 0; a < 255; a++)
            {
                for (int b = 0; b < 255; b++)
                {
                    INodeLifecycleManager? manager = _discoveryManager.GetNodeLifecycleManager(new Node(TestItem.PublicKeyA, $"{a}.{b}.1.1", 8000));
                    manager?.SendPingAsync();

                    PongMsg pongMsg = new(_publicKey, GetExpirationTime(), Array.Empty<byte>());
                    pongMsg.FarAddress = new IPEndPoint(IPAddress.Parse($"{a}.{b}.1.1"), Port);
                    _discoveryManager.OnIncomingMsg(pongMsg);
                }
            }
        }

        private static long GetExpirationTime() => Timestamper.Default.UnixTime.SecondsLong + 20;

        [Test, Ignore("Add bonding"), Retry(3)]
        public async Task OnNeighborsMessageTest()
        {
            //receiving pong to have a node in the system
            ReceiveSomePong();

            //expecting to activate node as valid peer
            IEnumerable<Node> nodes = _nodeTable.GetClosestNodes().ToArray();
            Assert.That(nodes.Count(), Is.EqualTo(1));
            Node node = nodes.First();
            Assert.That(node.Host, Is.EqualTo(Host));
            Assert.That(node.Port, Is.EqualTo(Port));
            INodeLifecycleManager? manager = _discoveryManager.GetNodeLifecycleManager(node);
            Assert.That(manager?.State, Is.EqualTo(NodeLifecycleState.Active));

            //sending FindNode to expect Neighbors
            manager?.SendFindNode(_nodeTable.MasterNode!.Id.Bytes);
            _msgSender.Received(1).SendMsg(Arg.Is<FindNodeMsg>(m => m.FarAddress!.Address.ToString() == Host && m.FarAddress.Port == Port));

            //receiving findNode
            NeighborsMsg msg = new(_publicKey, GetExpirationTime(), _nodes);
            msg.FarAddress = new IPEndPoint(IPAddress.Parse(Host), Port);
            _discoveryManager.OnIncomingMsg(msg);

            //expecting to send 3 pings to both nodes
            await Task.Delay(600);
            _msgSender.Received(3).SendMsg(Arg.Is<PingMsg>(m => m.FarAddress!.Address.ToString() == _nodes[0].Host && m.FarAddress.Port == _nodes[0].Port));
            _msgSender.Received(3).SendMsg(Arg.Is<PingMsg>(m => m.FarAddress!.Address.ToString() == _nodes[1].Host && m.FarAddress.Port == _nodes[1].Port));
        }

        private void ReceiveSomePong()
        {
            PongMsg pongMsg = new(_publicKey, GetExpirationTime(), Array.Empty<byte>());
            pongMsg.FarAddress = new IPEndPoint(IPAddress.Parse(Host), Port);
            _discoveryManager.OnIncomingMsg(pongMsg);
        }
    }
}
