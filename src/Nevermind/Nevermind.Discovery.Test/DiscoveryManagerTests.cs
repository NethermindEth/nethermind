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

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Nevermind.Core;
using Nevermind.Core.Crypto;
using Nevermind.Discovery.Lifecycle;
using Nevermind.Discovery.Messages;
using Nevermind.Discovery.RoutingTable;
using Nevermind.Json;
using Nevermind.KeyStore;
using NSubstitute;
using NSubstitute.Core;
using NUnit.Framework;

namespace Nevermind.Discovery.Test
{
    [TestFixture]
    public class DiscoveryManagerTests
    {
        private IMessageSerializer _messageSerializer;
        private IDiscoveryManager _discoveryManager;
        private IUdpClient _udpClient;
        private INodeTable _nodeTable;
        private INodeFactory _nodeFactory;
        private int _port = 1;
        private string _host = "TestHost";
        private Node[] _nodes;

        [SetUp]
        public void Initialize()
        {
            var logger = new ConsoleLogger();
            var config = new DiscoveryConfigurationProvider { PongTimeout = 100 };
            var configProvider = new ConfigurationProvider(Path.GetDirectoryName(Path.Combine(Path.GetTempPath(), "KeyStore")));
            _nodeFactory = new NodeFactory();
            var calculator = new NodeDistanceCalculator(config);

            _nodeTable = new NodeTable(config, _nodeFactory, new FileKeyStore(configProvider, new JsonSerializer(logger), new AesEncrypter(configProvider, logger), new CryptoRandom(), logger), logger, calculator);
            var evictionManager = new EvictionManager(_nodeTable);
            var lifecycleFactory = new NodeLifecycleManagerFactory(_nodeFactory, _nodeTable, logger, config, new MessageFactory(), evictionManager);

            _udpClient = Substitute.For<IUdpClient>();
            _udpClient.SubribeForMessages(Arg.Any<IUdpListener>());
            _udpClient.SendMessage(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<byte[]>());

            _messageSerializer = Substitute.For<IMessageSerializer>();            
            _messageSerializer.Serialize(Arg.Is<Message>(x => x.MessageType == MessageType.Ping)).Returns(new[] { (byte)MessageType.Ping });
            _messageSerializer.Serialize(Arg.Is<Message>(x => x.MessageType == MessageType.Pong)).Returns(new[] { (byte)MessageType.Pong });
            _messageSerializer.Serialize(Arg.Is<Message>(x => x.MessageType == MessageType.FindNode)).Returns(new[] { (byte)MessageType.FindNode });
            _messageSerializer.Serialize(Arg.Is<Message>(x => x.MessageType == MessageType.Neighbors)).Returns(new[] { (byte)MessageType.Neighbors });

            _messageSerializer.Deserialize(Arg.Is<byte[]>(x => x.First() == (byte)MessageType.Pong)).Returns(new PongMessage
            {
                Type = new[] { (byte)MessageType.Pong },
                Port = _port,
                Host = _host
            });
            _messageSerializer.Deserialize(Arg.Is<byte[]>(x => x.First() == (byte)MessageType.Ping)).Returns(new PingMessage
            {
                Type = new[] { (byte)MessageType.Ping },
                Port = _port,
                Host = _host
            });
            _messageSerializer.Deserialize(Arg.Is<byte[]>(x => x.First() == (byte)MessageType.FindNode)).Returns(new FindNodeMessage
            {
                Type = new[] { (byte)MessageType.FindNode },
                Port = _port,
                Host = _host
            });

            _nodes = new[] { _nodeFactory.CreateNode("TestHost1", 1), _nodeFactory.CreateNode("TestHost2", 2) };
            _messageSerializer.Deserialize(Arg.Is<byte[]>(x => x.First() == (byte)MessageType.Neighbors)).Returns(new NeighborsMessage
            {
                Type = new[] { (byte)MessageType.Neighbors },
                Nodes = _nodes,
                Port = _port,
                Host = _host
            });

            _discoveryManager = new DiscoveryManager(logger, config, lifecycleFactory, _nodeFactory, _messageSerializer, _udpClient);
        }

        [Test]
        public void OnPingMessageTest()
        {
            //receiving ping
            _discoveryManager.OnIncomingMessage(new[]{ (byte)MessageType.Ping });
            Thread.Sleep(400);

            //expecting to send pong
            _udpClient.Received(1).SendMessage(Arg.Is(_host), Arg.Is(_port), Arg.Is<byte[]>(x => x.First() == (byte)MessageType.Pong));

            //expecting to send 3 pings for every new node
            _udpClient.Received(3).SendMessage(Arg.Is(_host), Arg.Is(_port), Arg.Is<byte[]>(x => x.First() == (byte)MessageType.Ping));
        }

        [Test]
        public void OnPongMessageTest()
        {
            //receiving pong
            _discoveryManager.OnIncomingMessage(new[] { (byte)MessageType.Pong });
            
            //expecting to activate node as valid peer
            var nodes = _nodeTable.GetClosestNodes();
            Assert.AreEqual(1, nodes.Length);
            var node = nodes.First();
            Assert.AreEqual(_host, node.Host);
            Assert.AreEqual(_port, node.Port);
            var manager = _discoveryManager.GetNodeLifecycleManager(node);
            Assert.AreEqual(NodeLifecycleState.Active, manager.State);
        }

        [Test]
        public void OnFindNodeMessageTest()
        {
            //receiving pong to have a node in the system
            _discoveryManager.OnIncomingMessage(new[] { (byte)MessageType.Pong });

            //expecting to activate node as valid peer
            var nodes = _nodeTable.GetClosestNodes();
            Assert.AreEqual(1, nodes.Length);
            var node = nodes.First();
            Assert.AreEqual(_host, node.Host);
            Assert.AreEqual(_port, node.Port);
            var manager = _discoveryManager.GetNodeLifecycleManager(node);
            Assert.AreEqual(NodeLifecycleState.Active, manager.State);

            //receiving findNode
            _discoveryManager.OnIncomingMessage(new[] { (byte)MessageType.FindNode });

            //expecting to respond with sending Neighbors
            _udpClient.Received(1).SendMessage(Arg.Is(_host), Arg.Is(_port), Arg.Is<byte[]>(x => x.First() == (byte)MessageType.Neighbors));
        }

        [Test]
        public void OnNeighborsMessageTest()
        {
            //receiving pong to have a node in the system
            _discoveryManager.OnIncomingMessage(new[] { (byte)MessageType.Pong });

            //expecting to activate node as valid peer
            var nodes = _nodeTable.GetClosestNodes();
            Assert.AreEqual(1, nodes.Length);
            var node = nodes.First();
            Assert.AreEqual(_host, node.Host);
            Assert.AreEqual(_port, node.Port);
            var manager = _discoveryManager.GetNodeLifecycleManager(node);
            Assert.AreEqual(NodeLifecycleState.Active, manager.State);

            //sending FindNode to expect Neighbors
            manager.SendFindNode(_nodeTable.MasterNode);
            _udpClient.Received(1).SendMessage(Arg.Is(_host), Arg.Is(_port), Arg.Is<byte[]>(x => x.First() == (byte)MessageType.FindNode));

            //receiving findNode
            _discoveryManager.OnIncomingMessage(new[] { (byte)MessageType.Neighbors });

            //expecting to send 3 pings to both nodes
            Thread.Sleep(400);
            _udpClient.Received(3).SendMessage(Arg.Is(_nodes[0].Host), Arg.Is(_nodes[0].Port), Arg.Is<byte[]>(x => x.First() == (byte)MessageType.Ping));
            _udpClient.Received(3).SendMessage(Arg.Is(_nodes[1].Host), Arg.Is(_nodes[1].Port), Arg.Is<byte[]>(x => x.First() == (byte)MessageType.Ping));
        }
    }
}