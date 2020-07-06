//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Linq;
using System.Net;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Discovery
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class DiscoveryManagerTests
    {
        private const string TestPrivateKeyHex = "0x3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266";

        private INetworkConfig _networkConfig = new NetworkConfig();
        private IDiscoveryManager _discoveryManager;
        private IMessageSender _messageSender;
        private INodeTable _nodeTable;
        private ITimestamper _timestamper;
        private int _port = 1;
        private string _host = "192.168.1.17";
        private Node[] _nodes;
        private PublicKey _publicKey;
        private IIPResolver _ipResolver;

        [SetUp]
        public void Initialize()
        {
            NetworkNodeDecoder.Init();
            var privateKey = new PrivateKey(TestPrivateKeyHex);
            _publicKey = privateKey.PublicKey;
            var logManager = LimboLogs.Instance;

            IDiscoveryConfig discoveryConfig = new DiscoveryConfig();
            discoveryConfig.PongTimeout = 100;

            IStatsConfig statsConfig = new StatsConfig();

            _messageSender = Substitute.For<IMessageSender>();
            var calculator = new NodeDistanceCalculator(discoveryConfig);

            _networkConfig.ExternalIp = "99.10.10.66";
            _networkConfig.LocalIp = "10.0.0.5";

            _nodeTable = new NodeTable(calculator, discoveryConfig, _networkConfig, logManager);
            _nodeTable.Initialize(TestItem.PublicKeyA);

            _timestamper = Timestamper.Default;

            _ipResolver = new IPResolver(_networkConfig, logManager);

            var evictionManager = new EvictionManager(_nodeTable, logManager);
            var lifecycleFactory = new NodeLifecycleManagerFactory(_nodeTable, new DiscoveryMessageFactory(_timestamper), evictionManager, new NodeStatsManager(statsConfig, logManager), discoveryConfig, logManager);

            _nodes = new[] {new Node("192.168.1.18", 1), new Node("192.168.1.19", 2)};

            IFullDb nodeDb = new SimpleFilePublicKeyDb("Test", "test_db", logManager);
            _discoveryManager = new DiscoveryManager(lifecycleFactory, _nodeTable, new NetworkStorage(nodeDb, logManager), discoveryConfig, logManager, _ipResolver);
            _discoveryManager.MessageSender = _messageSender;
        }

        [Test, Retry(3)]
        public void OnPingMessageTest()
        {
            //receiving ping
            var address = new IPEndPoint(IPAddress.Parse(_host), _port);
            _discoveryManager.OnIncomingMessage(new PingMessage {FarAddress = address, FarPublicKey = _publicKey, DestinationAddress = _nodeTable.MasterNode.Address, SourceAddress = address});
            Thread.Sleep(500);

            // expecting to send pong
            _messageSender.Received(1).SendMessage(Arg.Is<PongMessage>(m => m.FarAddress.Address.ToString() == _host && m.FarAddress.Port == _port));

            // send pings to  new node
            _messageSender.Received().SendMessage(Arg.Is<PingMessage>(m => m.FarAddress.Address.ToString() == _host && m.FarAddress.Port == _port));
        }

        [Test, Ignore("Add bonding"), Retry(3)]
        public void OnPongMessageTest()
        {
            //receiving pong
            _discoveryManager.OnIncomingMessage(new PongMessage {FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port), FarPublicKey = _publicKey});

            //expecting to activate node as valid peer
            var nodes = _nodeTable.GetClosestNodes();
            Assert.AreEqual(1, nodes.Count());
            var node = nodes.First();
            Assert.AreEqual(_host, node.Host);
            Assert.AreEqual(_port, node.Port);
            var manager = _discoveryManager.GetNodeLifecycleManager(node);
            Assert.AreEqual(NodeLifecycleState.Active, manager.State);
        }

        [Test, Ignore("Add bonding"), Retry(3)]
        public void OnFindNodeMessageTest()
        {
            //receiving pong to have a node in the system
            _discoveryManager.OnIncomingMessage(new PongMessage {FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port), FarPublicKey = _publicKey});

            //expecting to activate node as valid peer
            var nodes = _nodeTable.GetClosestNodes();
            Assert.AreEqual(1, nodes.Count());
            var node = nodes.First();
            Assert.AreEqual(_host, node.Host);
            Assert.AreEqual(_port, node.Port);
            var manager = _discoveryManager.GetNodeLifecycleManager(node);
            Assert.AreEqual(NodeLifecycleState.Active, manager.State);

            //receiving findNode
            _discoveryManager.OnIncomingMessage(new FindNodeMessage {FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port), FarPublicKey = _publicKey, SearchedNodeId = Build.A.PrivateKey.TestObject.PublicKey.Bytes});

            //expecting to respond with sending Neighbors
            _messageSender.Received(1).SendMessage(Arg.Is<NeighborsMessage>(m => m.FarAddress.Address.ToString() == _host && m.FarAddress.Port == _port));
        }

        [Test, Retry(3)]
        public void MemoryTest()
        {
            //receiving pong to have a node in the system
            for (int a = 0; a < 255; a++)
            {
                for (int b = 0; b < 255; b++)
                {
                    INodeLifecycleManager manager = _discoveryManager.GetNodeLifecycleManager(new Node($"{a}.{b}.1.1", 8000));
                    manager.SendPingAsync();
                    _discoveryManager.OnIncomingMessage(new PongMessage {FarAddress = new IPEndPoint(IPAddress.Parse($"{a}.{b}.1.1"), _port), FarPublicKey = _publicKey});
                }
            }
        }

        [Test, Ignore("Add bonding"), Retry(3)]
        public void OnNeighborsMessageTest()
        {
            //receiving pong to have a node in the system
            _discoveryManager.OnIncomingMessage(new PongMessage {FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port), FarPublicKey = _publicKey});

            //expecting to activate node as valid peer
            var nodes = _nodeTable.GetClosestNodes();
            Assert.AreEqual(1, nodes.Count());
            var node = nodes.First();
            Assert.AreEqual(_host, node.Host);
            Assert.AreEqual(_port, node.Port);
            var manager = _discoveryManager.GetNodeLifecycleManager(node);
            Assert.AreEqual(NodeLifecycleState.Active, manager.State);

            //sending FindNode to expect Neighbors
            manager.SendFindNode(_nodeTable.MasterNode.Id.Bytes);
            _messageSender.Received(1).SendMessage(Arg.Is<FindNodeMessage>(m => m.FarAddress.Address.ToString() == _host && m.FarAddress.Port == _port));

            //receiving findNode
            _discoveryManager.OnIncomingMessage(new NeighborsMessage {FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port), FarPublicKey = _publicKey, Nodes = _nodes});

            //expecting to send 3 pings to both nodes
            Thread.Sleep(600);
            _messageSender.Received(3).SendMessage(Arg.Is<PingMessage>(m => m.FarAddress.Address.ToString() == _nodes[0].Host && m.FarAddress.Port == _nodes[0].Port));
            _messageSender.Received(3).SendMessage(Arg.Is<PingMessage>(m => m.FarAddress.Address.ToString() == _nodes[1].Host && m.FarAddress.Port == _nodes[1].Port));
        }
    }
}
