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

using System.Collections.Generic;
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

namespace Nevermind.Discovery.Test
{
    [TestFixture]
    public class NodeLifecycleManagerTests
    {
        private IMessageSerializer _messageSerializer;
        private IDiscoveryManager _discoveryManager;
        private IUdpClient _udpClient;
        private INodeTable _nodeTable;
        private INodeFactory _nodeFactory;
        private DiscoveryConfigurationProvider _configurationProvider;
        private int _port = 1;
        private string _host = "TestHost";

        [SetUp]
        public void Initialize()
        {
            var logger = new ConsoleLogger();
            //setting config to store 3 nodes in a bucket and for table to have one bucket//setting config to store 3 nodes in a bucket and for table to have one bucket
            _configurationProvider = new DiscoveryConfigurationProvider
            {
                PongTimeout = 100,
                BucketSize = 3,
                BucketsCount = 1
            }; 
            var configProvider = new ConfigurationProvider(Path.GetDirectoryName(Path.Combine(Path.GetTempPath(), "KeyStore")));
            _nodeFactory = new NodeFactory();
            var calculator = new NodeDistanceCalculator(_configurationProvider);

            _nodeTable = new NodeTable(_configurationProvider, _nodeFactory, new FileKeyStore(configProvider, new JsonSerializer(logger), new AesEncrypter(configProvider, logger), new CryptoRandom(), logger), logger, calculator);
            var evictionManager = new EvictionManager(_nodeTable, logger);
            var lifecycleFactory = new NodeLifecycleManagerFactory(_nodeFactory, _nodeTable, logger, _configurationProvider, new MessageFactory(), evictionManager);

            _udpClient = Substitute.For<IUdpClient>();
            _udpClient.SubribeForMessages(Arg.Any<IUdpListener>());
            _udpClient.SendMessage(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<byte[]>());

            _messageSerializer = Substitute.For<IMessageSerializer>();
            _messageSerializer.Serialize(Arg.Any<Message>()).Returns(x => new byte[] { });
            _discoveryManager = new DiscoveryManager(logger, _configurationProvider, lifecycleFactory, _nodeFactory, _messageSerializer, _udpClient);
        }

        [Test]
        public void ActiveStateTest()
        {
            var node = _nodeFactory.CreateNode(_host, _port);
            var manager = _discoveryManager.GetNodeLifecycleManager(node);
            Assert.AreEqual(NodeLifecycleState.New, manager.State);

            manager.ProcessPongMessage(new PongMessage{Type = new []{(byte)MessageType.Pong}, Host = _host, Port = _port});
            
            Assert.AreEqual(NodeLifecycleState.Active, manager.State);
        }

        [Test]
        public void UnreachableStateTest()
        {
            var node = _nodeFactory.CreateNode(_host, _port);
            var manager = _discoveryManager.GetNodeLifecycleManager(node);
            Assert.AreEqual(NodeLifecycleState.New, manager.State);

            Thread.Sleep(500);

            Assert.AreEqual(NodeLifecycleState.Unreachable, manager.State);
        }

        [Test]
        public void EvictCandidateStateWonEvictionTest()
        {
            //adding 3 active nodes
            var managers = new List<INodeLifecycleManager>();
            IDictionary<string, byte[]> rawIds = new Dictionary<string, byte[]>();
            for (var i = 0; i < 3; i++)
            {
                var host = _host + i;
                rawIds[host] = Encoding.UTF8.GetBytes("TestId" + i);
                var node = _nodeFactory.CreateNode(rawIds[host], host, _port);
                var manager = _discoveryManager.GetNodeLifecycleManager(node);
                managers.Add(manager);
                Assert.AreEqual(NodeLifecycleState.New, manager.State);

                _messageSerializer.Deserialize(Arg.Any<byte[]>()).Returns(new PongMessage
                {
                    Type = new[] { (byte)MessageType.Pong },
                    Port = _port,
                    Host = host,
                    Signature = rawIds[host]
                });
                _discoveryManager.OnIncomingMessage(new byte[1]);

                Assert.AreEqual(NodeLifecycleState.Active, manager.State);
            }

            //table should contain 3 active nodes
            var closestNodes = _nodeTable.GetClosestNodes();
            Assert.IsTrue(closestNodes.Count(x => x.Host == managers[0].ManagedNode.Host) == 1);
            Assert.IsTrue(closestNodes.Count(x => x.Host == managers[1].ManagedNode.Host) == 1);
            Assert.IsTrue(closestNodes.Count(x => x.Host == managers[2].ManagedNode.Host) == 1);

            //adding 4th node - table can store only 3, eviction process should start
            var candidateNodeId = Encoding.UTF8.GetBytes("TestId");
            var candidateNode = _nodeFactory.CreateNode(candidateNodeId, _host, _port);
            var candidateManager = _discoveryManager.GetNodeLifecycleManager(candidateNode);

            Assert.AreEqual(NodeLifecycleState.New, candidateManager.State);
            _messageSerializer.Deserialize(Arg.Any<byte[]>()).Returns(new PongMessage
            {
                Type = new[] { (byte)MessageType.Pong },
                Port = _port,
                Host = _host,
                Signature = candidateNodeId
            });
            _discoveryManager.OnIncomingMessage(new byte[1]);
            Assert.AreEqual(NodeLifecycleState.Active, candidateManager.State);
            var evictionCandidate = managers.First(x => x.State == NodeLifecycleState.EvictCandidate);

            //receiving pong for eviction candidate - should survive
            _messageSerializer.Deserialize(Arg.Any<byte[]>()).Returns(new PongMessage
            {
                Type = new[] { (byte)MessageType.Pong },
                Port = _port,
                Host = evictionCandidate.ManagedNode.Host,
                Signature = rawIds[evictionCandidate.ManagedNode.Host]
            });
            _discoveryManager.OnIncomingMessage(new byte[1]);

            Thread.Sleep(1000);

            //3th node should survive, 4th node should be active but not in the table
            Assert.AreEqual(NodeLifecycleState.Active, candidateManager.State);
            Assert.AreEqual(NodeLifecycleState.Active, evictionCandidate.State);
            closestNodes = _nodeTable.GetClosestNodes();
            Assert.IsTrue(closestNodes.Count(x => x.Host == managers[0].ManagedNode.Host) == 1);
            Assert.IsTrue(closestNodes.Count(x => x.Host == managers[1].ManagedNode.Host) == 1);
            Assert.IsTrue(closestNodes.Count(x => x.Host == managers[2].ManagedNode.Host) == 1);
            Assert.IsTrue(closestNodes.Count(x => x.Host == candidateNode.Host) == 0);
        }

        [Test]
        public void EvictCandidateStateLostEvictionTest()
        {
            //adding 3 active nodes
            var managers = new List<INodeLifecycleManager>();
            for (var i = 0; i < 3; i++)
            {
                var host = _host + i;
                var rawId = Encoding.UTF8.GetBytes("TestId" + i);
                var node = _nodeFactory.CreateNode(rawId, host, _port);
                var manager = _discoveryManager.GetNodeLifecycleManager(node);
                managers.Add(manager);
                Assert.AreEqual(NodeLifecycleState.New, manager.State);

                _messageSerializer.Deserialize(Arg.Any<byte[]>()).Returns(new PongMessage
                {
                    Type = new[] { (byte)MessageType.Pong },
                    Port = _port,
                    Host = host,
                    Signature = rawId
                });
                _discoveryManager.OnIncomingMessage(new byte[1]);

                Assert.AreEqual(NodeLifecycleState.Active, manager.State);
            }

            //table should contain 3 active nodes
            var closestNodes = _nodeTable.GetClosestNodes();
            Assert.IsTrue(closestNodes.Count(x => x.Host == managers[0].ManagedNode.Host) == 1);
            Assert.IsTrue(closestNodes.Count(x => x.Host == managers[1].ManagedNode.Host) == 1);
            Assert.IsTrue(closestNodes.Count(x => x.Host == managers[2].ManagedNode.Host) == 1);

            //adding 4th node - table can store only 3, eviction process should start
            var candidateNodeId = Encoding.UTF8.GetBytes("TestId");
            var candidateNode = _nodeFactory.CreateNode(candidateNodeId, _host, _port);
            var candidateManager = _discoveryManager.GetNodeLifecycleManager(candidateNode);

            Assert.AreEqual(NodeLifecycleState.New, candidateManager.State);
            _messageSerializer.Deserialize(Arg.Any<byte[]>()).Returns(new PongMessage
            {
                Type = new[] { (byte)MessageType.Pong },
                Port = _port,
                Host = _host,
                Signature = candidateNodeId
            });
            _discoveryManager.OnIncomingMessage(new byte[1]);
            Thread.Sleep(10);
            Assert.AreEqual(NodeLifecycleState.Active, candidateManager.State);
            var evictionCandidate = managers.First(x => x.State == NodeLifecycleState.EvictCandidate);

            Thread.Sleep(1000);

            //3th node should be evicted, 4th node should be added to the table
            Assert.AreEqual(NodeLifecycleState.Active, candidateManager.State);
            Assert.AreEqual(NodeLifecycleState.Unreachable, evictionCandidate.State);
            closestNodes = _nodeTable.GetClosestNodes();
            Assert.IsTrue(managers.Where(x => x.State == NodeLifecycleState.Active).All(x => closestNodes.Any(y => y.Host == x.ManagedNode.Host)));
            Assert.IsTrue(closestNodes.Count(x => x.Host == evictionCandidate.ManagedNode.Host) == 0);
            Assert.IsTrue(closestNodes.Count(x => x.Host == candidateNode.Host) == 1);
        }
    }
}