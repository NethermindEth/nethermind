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

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
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
    public class NodeLifecycleManagerTests
    {
        private Signature[] _signatureMocks;
        private PublicKey[] _nodeIds;
        private INodeStats _nodeStatsMock;
        private Dictionary<string, PublicKey> _signatureToNodeId;

        private INetworkConfig _networkConfig = new NetworkConfig();
        private IDiscoveryManager _discoveryManager;
        private IDiscoveryManager _discoveryManagerMock;
        private IDiscoveryConfig _discoveryConfigMock;
        private IMessageSender _udpClient;
        private INodeTable _nodeTable;
        private IConfigProvider _configurationProvider;
        private ITimestamper _timestamper;
        private IEvictionManager _evictionManagerMock;
        private ILogger _loggerMock;
        private IIPResolver _ipResolverMock;
        private int _port = 1;
        private string _host = "192.168.1.27";

        [SetUp]
        public void Initialize()
        {
            _discoveryManagerMock = Substitute.For<IDiscoveryManager>();
            _discoveryConfigMock = Substitute.For<IDiscoveryConfig>();
            
            
            NetworkNodeDecoder.Init();
            SetupNodeIds();

            var logManager = LimboLogs.Instance;
            _loggerMock = Substitute.For<ILogger>();
            //setting config to store 3 nodes in a bucket and for table to have one bucket//setting config to store 3 nodes in a bucket and for table to have one bucket

            _configurationProvider = new ConfigProvider();
            _networkConfig.ExternalIp = "99.10.10.66";
            _networkConfig.LocalIp = "10.0.0.5";
            
            IDiscoveryConfig discoveryConfig = _configurationProvider.GetConfig<IDiscoveryConfig>();
            discoveryConfig.PongTimeout = 50;
            discoveryConfig.BucketSize = 3;
            discoveryConfig.BucketsCount = 1;

            _ipResolverMock = Substitute.For<IIPResolver>();

            var calculator = new NodeDistanceCalculator(discoveryConfig);

            _nodeTable = new NodeTable(calculator, discoveryConfig, _networkConfig, logManager);
            _nodeTable.Initialize(TestItem.PublicKeyA);
            _nodeStatsMock = Substitute.For<INodeStats>();
            
            _timestamper = Timestamper.Default;

            var evictionManager = new EvictionManager(_nodeTable, logManager);
            _evictionManagerMock = Substitute.For<IEvictionManager>();
            ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
            var lifecycleFactory = new NodeLifecycleManagerFactory(_nodeTable, new DiscoveryMessageFactory(_timestamper), evictionManager, 
                new NodeStatsManager(timerFactory, logManager), discoveryConfig, logManager);

            _udpClient = Substitute.For<IMessageSender>();

            var discoveryDb = new SimpleFilePublicKeyDb("Test","test", logManager);
            _discoveryManager = new DiscoveryManager(lifecycleFactory, _nodeTable, new NetworkStorage(discoveryDb, logManager), discoveryConfig, logManager, _ipResolverMock);
            _discoveryManager.MessageSender = _udpClient;

            _discoveryManagerMock = Substitute.For<IDiscoveryManager>();
        }

        [Test]
        public async Task sending_ping_recieving_proper_pong_sets_bounded()
        {
            var node = new Node(_host, _port);
            var nodeManager = new NodeLifecycleManager(node, _discoveryManagerMock
            , _nodeTable, new DiscoveryMessageFactory(_timestamper), _evictionManagerMock, _nodeStatsMock, _discoveryConfigMock, _loggerMock);

            var sentPing = new PingMessage();
            _discoveryManagerMock.SendMessage(Arg.Do<PingMessage>(msg => sentPing = msg));

            await nodeManager.SendPingAsync();
            nodeManager.ProcessPongMessage(new PongMessage{ PingMdc = sentPing.Mdc });

            Assert.IsTrue(nodeManager.IsBonded);
        }

        [Test]
        public async Task sending_ping_recieving_incorect_pong_does_not_bond()
        {
            var node = new Node(_host, _port);
            var nodeManager = new NodeLifecycleManager(node, _discoveryManagerMock
            , _nodeTable, new DiscoveryMessageFactory(_timestamper), _evictionManagerMock, _nodeStatsMock, _discoveryConfigMock, _loggerMock);

            PingMessage sentPing = new PingMessage();
            _discoveryManagerMock.SendMessage(Arg.Do<PingMessage>(msg => sentPing = msg));

            await nodeManager.SendPingAsync();
            nodeManager.ProcessPongMessage(new PongMessage{ PingMdc = new byte[] {1,1,1} });

            Assert.IsFalse(nodeManager.IsBonded);
        }

        [Test]
        public void Wrong_pong_will_get_ignored()
        {
            var node = new Node(_host, _port);
            var manager = _discoveryManager.GetNodeLifecycleManager(node);
            Assert.AreEqual(NodeLifecycleState.New, manager.State);
            
            manager.ProcessPongMessage(new PongMessage {FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port), FarPublicKey = _nodeIds[0], PingMdc = new byte[32]});

            Assert.AreEqual(NodeLifecycleState.New, manager.State);
        }

        [Test]
        [Retry(3)]
        public void UnreachableStateTest()
        {
            var node = new Node(_host, _port);
            var manager = _discoveryManager.GetNodeLifecycleManager(node);
            Assert.AreEqual(NodeLifecycleState.New, manager.State);

            //await Task.Delay(500);

            Assert.That(() => manager.State, Is.EqualTo(NodeLifecycleState.Unreachable).After(500, 50));
            //Assert.AreEqual(NodeLifecycleState.Unreachable, manager.State);
        }

        [Test, Retry(3), Ignore("Eviction changes were introduced and we would need to expose some internals to test bonding")]
        public void EvictCandidateStateWonEvictionTest()
        {
            //adding 3 active nodes
            var managers = new List<INodeLifecycleManager>();
            for (var i = 0; i < 3; i++)
            {
                var host = "192.168.1." + i;
                var node = new Node(_nodeIds[i], host, _port);
                var manager = _discoveryManager.GetNodeLifecycleManager(node);
                managers.Add(manager);
                Assert.AreEqual(NodeLifecycleState.New, manager.State);

                _discoveryManager.OnIncomingMessage(new PongMessage { FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port), FarPublicKey = _nodeIds[i] });
                Assert.AreEqual(NodeLifecycleState.New, manager.State);
            }

            //table should contain 3 active nodes
            var closestNodes = _nodeTable.GetClosestNodes();
            Assert.IsTrue(closestNodes.Count(x => x.Host == managers[0].ManagedNode.Host) == 0);
            Assert.IsTrue(closestNodes.Count(x => x.Host == managers[1].ManagedNode.Host) == 0);
            Assert.IsTrue(closestNodes.Count(x => x.Host == managers[2].ManagedNode.Host) == 0);

            //adding 4th node - table can store only 3, eviction process should start
            var candidateNode = new Node(_nodeIds[3], _host, _port);
            var candidateManager = _discoveryManager.GetNodeLifecycleManager(candidateNode);

            Assert.AreEqual(NodeLifecycleState.New, candidateManager.State);

            _discoveryManager.OnIncomingMessage(new PongMessage { FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port), FarPublicKey = _nodeIds[3]});
            Assert.AreEqual(NodeLifecycleState.New, candidateManager.State);
            var evictionCandidate = managers.First(x => x.State == NodeLifecycleState.EvictCandidate);

            //receiving pong for eviction candidate - should survive
            _discoveryManager.OnIncomingMessage(new PongMessage { FarAddress = new IPEndPoint(IPAddress.Parse(evictionCandidate.ManagedNode.Host), _port), FarPublicKey = evictionCandidate.ManagedNode.Id });

            //await Task.Delay(100);

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
        [Ignore("This test keeps failing and should be only manually reenabled / understood when we review the discovery code")]
        public void EvictCandidateStateLostEvictionTest()
        {
            //adding 3 active nodes
            var managers = new List<INodeLifecycleManager>();
            for (var i = 0; i < 3; i++)
            {
                var host = "192.168.1." + i;
                var node = new Node(_nodeIds[i], host, _port);
                var manager = _discoveryManager.GetNodeLifecycleManager(node);
                managers.Add(manager);
                Assert.AreEqual(NodeLifecycleState.New, manager.State);

                _discoveryManager.OnIncomingMessage(new PongMessage { FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port), FarPublicKey = _nodeIds[i] });

                Assert.AreEqual(NodeLifecycleState.Active, manager.State);
            }

            //table should contain 3 active nodes
            var closestNodes = _nodeTable.GetClosestNodes();
            for (int i = 0; i < 3; i++)
            {
                Assert.IsTrue(closestNodes.Count(x => x.Host == managers[0].ManagedNode.Host) == 1);
            }

            //adding 4th node - table can store only 3, eviction process should start
            var candidateNode = new Node(_nodeIds[3], _host, _port);

            var candidateManager = _discoveryManager.GetNodeLifecycleManager(candidateNode);
            Assert.AreEqual(NodeLifecycleState.New, candidateManager.State);
            _discoveryManager.OnIncomingMessage(new PongMessage
            {
                FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port),
                FarPublicKey = _nodeIds[3]
            });

            //await Task.Delay(10);
            Assert.That(() => candidateManager.State, Is.EqualTo(NodeLifecycleState.Active).After(10, 5));
            //Assert.AreEqual(NodeLifecycleState.Active, candidateManager.State);

            var evictionCandidate = managers.First(x => x.State == NodeLifecycleState.EvictCandidate);
            //await Task.Delay(300);

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
            _nodeIds = new PublicKey[4];

            for (int i = 0; i < 4; i++)
            {
                byte[] signatureBytes = new byte[65];
                signatureBytes[64] = (byte)i;
                _signatureMocks[i] = new Signature(signatureBytes);

                byte[] nodeIdBytes = new byte[64];
                nodeIdBytes[63] = (byte)i;
                _nodeIds[i] = new PublicKey(nodeIdBytes);

                _signatureToNodeId.Add(_signatureMocks[i].ToString(), _nodeIds[i]);
            }
        }
    }
}
