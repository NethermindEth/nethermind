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
using NUnit.Framework;
using Node = Nevermind.Discovery.RoutingTable.Node;
using PingMessage = Nevermind.Discovery.Messages.PingMessage;
using PongMessage = Nevermind.Discovery.Messages.PongMessage;

namespace Nevermind.Discovery.Test
{   
    [TestFixture]
    public class DiscoveryManagerTests
    {   
        private INodeIdResolver _nodeIdResolver;
        private IDiscoveryManager _discoveryManager;
        private IMessageSender _messageSender;
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
            _nodeIdResolver = Substitute.For<INodeIdResolver>();
            _nodeIdResolver.GetNodeId(Arg.Any<DiscoveryMessage>()).Returns(info =>
            {
                byte[] hostBytes = Encoding.UTF8.GetBytes(info.Arg<DiscoveryMessage>().Host);
                Array.Resize(ref hostBytes, 64);
                return new PublicKey(hostBytes);
            });
            
            _messageSender = Substitute.For<IMessageSender>();
            _nodeFactory = new NodeFactory();
            var calculator = new NodeDistanceCalculator(config);

            _nodeTable = new NodeTable(config, _nodeFactory, new FileKeyStore(configProvider, new JsonSerializer(logger), new AesEncrypter(configProvider, logger), new CryptoRandom(), logger), logger, calculator);
            var evictionManager = new EvictionManager(_nodeTable, logger);
            var lifecycleFactory = new NodeLifecycleManagerFactory(_nodeFactory, _nodeTable, logger, config, new MessageFactory(), evictionManager);

            _nodes = new[] { _nodeFactory.CreateNode("TestHost1", 1), _nodeFactory.CreateNode("TestHost2", 2) };

            _discoveryManager = new DiscoveryManager(logger, config, lifecycleFactory, _nodeFactory, _messageSender, _nodeIdResolver);
        }

        [Test]
        public void OnPingMessageTest()
        {
            //receiving ping
            _discoveryManager.OnIncomingMessage(new PingMessage{Port = _port, Host = _host});
            Thread.Sleep(400);

            //expecting to send pong
            _messageSender.Received(1).SendMessage(Arg.Is<PongMessage>(m => m.Host == _host && m.Port == _port));

            //expecting to send 3 pings for every new node
            _messageSender.Received(3).SendMessage(Arg.Is<PingMessage>(m => m.Host == _host && m.Port == _port));
        }

        [Test]
        public void OnPongMessageTest()
        {
            //receiving pong
            _discoveryManager.OnIncomingMessage(new PongMessage{Port = _port, Host = _host});
            
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
            _discoveryManager.OnIncomingMessage(new PongMessage{Port = _port, Host = _host});

            //expecting to activate node as valid peer
            var nodes = _nodeTable.GetClosestNodes();
            Assert.AreEqual(1, nodes.Length);
            var node = nodes.First();
            Assert.AreEqual(_host, node.Host);
            Assert.AreEqual(_port, node.Port);
            var manager = _discoveryManager.GetNodeLifecycleManager(node);
            Assert.AreEqual(NodeLifecycleState.Active, manager.State);

            //receiving findNode
            _discoveryManager.OnIncomingMessage(new FindNodeMessage{Port = _port, Host = _host});

            //expecting to respond with sending Neighbors
            _messageSender.Received(1).SendMessage(Arg.Is<NeighborsMessage>(m => m.Host == _host && m.Port == _port));
        }

        [Test]
        public void OnNeighborsMessageTest()
        {
            //receiving pong to have a node in the system
            _discoveryManager.OnIncomingMessage(new PongMessage{Port = _port, Host = _host});

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
            _messageSender.Received(1).SendMessage(Arg.Is<FindNodeMessage>(m => m.Host == _host && m.Port == _port));

            //receiving findNode
            _discoveryManager.OnIncomingMessage(new NeighborsMessage{Port = _port, Host = _host, Nodes = _nodes});

            //expecting to send 3 pings to both nodes
            Thread.Sleep(400);
            _messageSender.Received(3).SendMessage(Arg.Is<PingMessage>(m => m.Host == _nodes[0].Host && m.Port == _nodes[0].Port));
            _messageSender.Received(3).SendMessage(Arg.Is<PingMessage>(m => m.Host == _nodes[1].Host && m.Port == _nodes[1].Port));
        }
    }
}