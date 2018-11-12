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
using System.Linq;
using System.Net;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Model;
using Nethermind.KeyStore;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Lifecycle;
using Nethermind.Network.Discovery.Messages;
using Nethermind.Network.Discovery.RoutingTable;
using Nethermind.Stats;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.Discovery
{
    [TestFixture]
    public class NodeLifecycleManagerTests
    {
        private const string TestPrivateKeyHex = "0x3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266";

        private Signature[] _signatureMocks;
        private NodeId[] _nodeIds;
        private Dictionary<string, PublicKey> _signatureToNodeId;

        private IDiscoveryManager _discoveryManager;
        private IMessageSender _udpClient;
        private INodeTable _nodeTable;
        private INodeFactory _nodeFactory;
        private IConfigProvider _configurationProvider;
        private ITimestamp _timestamp;
        private int _port = 1;
        private string _host = "192.168.1.27";

        [SetUp]
        public void Initialize()
        {
            SetupNodeIds();

            var logManager = NullLogManager.Instance;
            //setting config to store 3 nodes in a bucket and for table to have one bucket//setting config to store 3 nodes in a bucket and for table to have one bucket

            _configurationProvider = new JsonConfigProvider();
            INetworkConfig networkConfig = _configurationProvider.GetConfig<INetworkConfig>();
            networkConfig.PongTimeout = 50;
            networkConfig.BucketSize = 3;
            networkConfig.BucketsCount = 1;

            _nodeFactory = new NodeFactory();
            var calculator = new NodeDistanceCalculator(_configurationProvider);

            _nodeTable = new NodeTable(_nodeFactory, new FileKeyStore(_configurationProvider, new JsonSerializer(logManager), new AesEncrypter(_configurationProvider, logManager), new CryptoRandom(), logManager), calculator, _configurationProvider, logManager);
            _nodeTable.Initialize();
            
            _timestamp = new Timestamp();

            var evictionManager = new EvictionManager(_nodeTable, logManager);
            var lifecycleFactory = new NodeLifecycleManagerFactory(_nodeFactory, _nodeTable, new DiscoveryMessageFactory(_configurationProvider, _timestamp), evictionManager, new NodeStatsProvider(_configurationProvider.GetConfig<IStatsConfig>(), _nodeFactory, logManager), _configurationProvider, logManager);

            _udpClient = Substitute.For<IMessageSender>();

            _discoveryManager = new DiscoveryManager(lifecycleFactory, _nodeFactory, _nodeTable, new NetworkStorage("test", networkConfig, logManager, new PerfService(logManager)), _configurationProvider, logManager);
            _discoveryManager.MessageSender = _udpClient;
        }

        [Test]
        public void ActiveStateTest()
        {
            var node = _nodeFactory.CreateNode(_host, _port);
            var manager = _discoveryManager.GetNodeLifecycleManager(node);
            Assert.AreEqual(NodeLifecycleState.New, manager.State);

            manager.ProcessPongMessage(new PongMessage {FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port), FarPublicKey = _nodeIds[0].PublicKey });

            Assert.AreEqual(NodeLifecycleState.Active, manager.State);
        }

        [Test]
        public void UnreachableStateTest()
        {
            var node = _nodeFactory.CreateNode(_host, _port);
            var manager = _discoveryManager.GetNodeLifecycleManager(node);
            Assert.AreEqual(NodeLifecycleState.New, manager.State);

            //Thread.Sleep(500);

            Assert.That(() => manager.State, Is.EqualTo(NodeLifecycleState.Unreachable).After(500, 50));
            //Assert.AreEqual(NodeLifecycleState.Unreachable, manager.State);
        }

        [Test]
        public void EvictCandidateStateWonEvictionTest()
        {
            //adding 3 active nodes
            var managers = new List<INodeLifecycleManager>();
            for (var i = 0; i < 3; i++)
            {
                var host = "192.168.1." + i;
                var node = _nodeFactory.CreateNode(_nodeIds[i], host, _port);
                var manager = _discoveryManager.GetNodeLifecycleManager(node);
                managers.Add(manager);
                Assert.AreEqual(NodeLifecycleState.New, manager.State);

                _discoveryManager.OnIncomingMessage(new PongMessage { FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port), FarPublicKey = _nodeIds[i].PublicKey });
                Assert.AreEqual(NodeLifecycleState.Active, manager.State);
            }

            //table should contain 3 active nodes
            var closestNodes = _nodeTable.GetClosestNodes();
            Assert.IsTrue(closestNodes.Count(x => x.Host == managers[0].ManagedNode.Host) == 1);
            Assert.IsTrue(closestNodes.Count(x => x.Host == managers[1].ManagedNode.Host) == 1);
            Assert.IsTrue(closestNodes.Count(x => x.Host == managers[2].ManagedNode.Host) == 1);

            //adding 4th node - table can store only 3, eviction process should start
            var candidateNode = _nodeFactory.CreateNode(_nodeIds[3], _host, _port);
            var candidateManager = _discoveryManager.GetNodeLifecycleManager(candidateNode);

            Assert.AreEqual(NodeLifecycleState.New, candidateManager.State);

            _discoveryManager.OnIncomingMessage(new PongMessage { FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port), FarPublicKey = _nodeIds[3].PublicKey });
            Assert.AreEqual(NodeLifecycleState.Active, candidateManager.State);
            var evictionCandidate = managers.First(x => x.State == NodeLifecycleState.EvictCandidate);

            //receiving pong for eviction candidate - should survive
            _discoveryManager.OnIncomingMessage(new PongMessage { FarAddress = new IPEndPoint(IPAddress.Parse(evictionCandidate.ManagedNode.Host), _port), FarPublicKey = evictionCandidate.ManagedNode.Id.PublicKey });

            //Thread.Sleep(100);

            //3th node should survive, 4th node should be active but not in the table
            Assert.That(() => candidateManager.State, Is.EqualTo(NodeLifecycleState.ActiveExcluded).After(100, 50));
            Assert.That(() => evictionCandidate.State, Is.EqualTo(NodeLifecycleState.Active).After(100, 50));

            //Assert.AreEqual(NodeLifecycleState.ActiveExcluded, candidateManager.State);
            //Assert.AreEqual(NodeLifecycleState.Active, evictionCandidate.State);
            closestNodes = _nodeTable.GetClosestNodes();
            Assert.That(() => closestNodes.Count(x => x.Host == managers[0].ManagedNode.Host) == 1, Is.True.After(100, 50));
            Assert.That(() => closestNodes.Count(x => x.Host == managers[1].ManagedNode.Host) == 1, Is.True.After(100, 50));
            Assert.That(() => closestNodes.Count(x => x.Host == managers[2].ManagedNode.Host) == 1, Is.True.After(100, 50));
            Assert.That(() => closestNodes.Count(x => x.Host == candidateNode.Host) == 0, Is.True.After(100, 50));
            
            //Assert.IsTrue(closestNodes.Count(x => x.Host == managers[0].ManagedNode.Host) == 1);
            //Assert.IsTrue(closestNodes.Count(x => x.Host == managers[1].ManagedNode.Host) == 1);
            //Assert.IsTrue(closestNodes.Count(x => x.Host == managers[2].ManagedNode.Host) == 1);
            //Assert.IsTrue(closestNodes.Count(x => x.Host == candidateNode.Host) == 0);
        }

        [Test]
        public void EvictCandidateStateLostEvictionTest()
        {
            //adding 3 active nodes
            var managers = new List<INodeLifecycleManager>();
            for (var i = 0; i < 3; i++)
            {
                var host = "192.168.1." + i;
                var node = _nodeFactory.CreateNode(_nodeIds[i], host, _port);
                var manager = _discoveryManager.GetNodeLifecycleManager(node);
                managers.Add(manager);
                Assert.AreEqual(NodeLifecycleState.New, manager.State);

                _discoveryManager.OnIncomingMessage(new PongMessage { FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port), FarPublicKey = _nodeIds[i].PublicKey });

                Assert.AreEqual(NodeLifecycleState.Active, manager.State);
            }

            //table should contain 3 active nodes
            var closestNodes = _nodeTable.GetClosestNodes();
            for (int i = 0; i < 3; i++)
            {
                Assert.IsTrue(closestNodes.Count(x => x.Host == managers[0].ManagedNode.Host) == 1);
            }

            //adding 4th node - table can store only 3, eviction process should start
            var candidateNode = _nodeFactory.CreateNode(_nodeIds[3], _host, _port);

            var candidateManager = _discoveryManager.GetNodeLifecycleManager(candidateNode);
            Assert.AreEqual(NodeLifecycleState.New, candidateManager.State);
            _discoveryManager.OnIncomingMessage(new PongMessage
            {
                FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port),
                FarPublicKey = _nodeIds[3].PublicKey
            });

            //Thread.Sleep(10);
            Assert.That(() => candidateManager.State, Is.EqualTo(NodeLifecycleState.Active).After(10, 5));
            //Assert.AreEqual(NodeLifecycleState.Active, candidateManager.State);

            var evictionCandidate = managers.First(x => x.State == NodeLifecycleState.EvictCandidate);
            //Thread.Sleep(300);

            //3th node should be evicted, 4th node should be added to the table
            //Assert.AreEqual(NodeLifecycleState.Active, candidateManager.State);
            Assert.That(() => candidateManager.State, Is.EqualTo(NodeLifecycleState.Active).After(300, 50));
            //Assert.AreEqual(NodeLifecycleState.Unreachable, evictionCandidate.State);
            Assert.That(() => evictionCandidate.State, Is.EqualTo(NodeLifecycleState.Unreachable).After(300, 50));

            closestNodes = _nodeTable.GetClosestNodes();
            Assert.That(() => managers.Where(x => x.State == NodeLifecycleState.Active).All(x => closestNodes.Any(y => y.Host == x.ManagedNode.Host)), Is.True.After(300, 50));
            Assert.That(() => closestNodes.Count(x => x.Host == evictionCandidate.ManagedNode.Host) == 0, Is.True.After(300, 50));
            Assert.That(() => closestNodes.Count(x => x.Host == candidateNode.Host) == 1, Is.True.After(300, 50));

            //Assert.IsTrue(managers.Where(x => x.State == NodeLifecycleState.Active).All(x => closestNodes.Any(y => y.Host == x.ManagedNode.Host)));
            //Assert.IsTrue(closestNodes.Count(x => x.Host == evictionCandidate.ManagedNode.Host) == 0);
            //Assert.IsTrue(closestNodes.Count(x => x.Host == candidateNode.Host) == 1);
        }

        private void SetupNodeIds()
        {
            _signatureToNodeId = new Dictionary<string, PublicKey>();
            _signatureMocks = new Signature[4];
            _nodeIds = new NodeId[4];

            for (int i = 0; i < 4; i++)
            {
                byte[] signatureBytes = new byte[65];
                signatureBytes[64] = (byte)i;
                _signatureMocks[i] = new Signature(signatureBytes);

                byte[] nodeIdBytes = new byte[64];
                nodeIdBytes[63] = (byte)i;
                _nodeIds[i] = new NodeId(new PublicKey(nodeIdBytes));

                _signatureToNodeId.Add(_signatureMocks[i].ToString(), _nodeIds[i].PublicKey);
            }
        }
    }
}