// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Config;
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
            SetupDiscoveryManager();
        }

        private void SetupDiscoveryManager(IDiscoveryConfig? config = null)
        {
            NetworkNodeDecoder.Init();
            PrivateKey privateKey = new(TestPrivateKeyHex);
            _publicKey = privateKey.PublicKey;
            LimboLogs? logManager = LimboLogs.Instance;

            IDiscoveryConfig discoveryConfig = config ?? new DiscoveryConfig();
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
            _discoveryManager = new DiscoveryManager(lifecycleFactory, _nodeTable, new NetworkStorage(nodeDb, logManager), discoveryConfig, new NetworkConfig(), logManager);
            _discoveryManager.MsgSender = _msgSender;
        }

        [Test, Retry(3)]
        public async Task OnPingMessageTest()
        {
            //receiving ping
            IPEndPoint address = new(IPAddress.Parse(Host), Port);
            _discoveryManager.OnIncomingMsg(new PingMsg(_publicKey, GetExpirationTime(), address, _nodeTable.MasterNode!.Address, new byte[32]) { FarAddress = address });

            // expecting to send pong
            Assert.That(() => _msgSender.ReceivedCallsMatching(s => s.SendMsg(Arg.Is<PongMsg>(static m => m.FarAddress!.Address.ToString() == Host && m.FarAddress.Port == Port))), Is.True.After(500, 10));

            // send pings to  new node
            await _msgSender.Received().SendMsg(Arg.Is<PingMsg>(static m => m.FarAddress!.Address.ToString() == Host && m.FarAddress.Port == Port));
        }

        [Test]
        public void OnPingMessage_WithUnexpectedDestination_IsIgnored()
        {
            IPEndPoint address = new(IPAddress.Parse(Host), Port);
            PingMsg ping = new(_publicKey, GetExpirationTime(), address, new IPEndPoint(IPAddress.Parse("203.0.113.9"), _nodeTable.MasterNode!.Port), new byte[32])
            {
                FarAddress = address
            };

            _discoveryManager.OnIncomingMsg(ping);

            _msgSender.DidNotReceive().SendMsg(Arg.Any<DiscoveryMsg>());
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
            _msgSender.Received(1).SendMsg(Arg.Is<NeighborsMsg>(static m => m.FarAddress!.Address.ToString() == Host && m.FarAddress.Port == Port));
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

                    PongMsg pongMsg = new(_publicKey, GetExpirationTime(), []);
                    pongMsg.FarAddress = new IPEndPoint(IPAddress.Parse($"{a}.{b}.1.1"), Port);
                    _discoveryManager.OnIncomingMsg(pongMsg);
                }
            }
        }

        [Test]
        public void OnPingMessage_FromFilteredIp_IsIgnored()
        {
            // Setup with filtering enabled (default) — the filter has a 5-minute timeout per IP/subnet.
            // First ping from this IP creates a lifecycle manager and is processed.
            IPEndPoint address = new(IPAddress.Parse("203.0.113.50"), Port);
            PingMsg ping1 = new(TestItem.PublicKeyC, GetExpirationTime(), address, _nodeTable.MasterNode!.Address, new byte[32])
            {
                FarAddress = address
            };
            _discoveryManager.OnIncomingMsg(ping1);

            // Second ping from the same IP but different node ID should be filtered
            // because the IP is already in the NodeFilter cache.
            PingMsg ping2 = new(TestItem.PublicKeyD, GetExpirationTime(), address, _nodeTable.MasterNode!.Address, new byte[32])
            {
                FarAddress = address
            };
            _discoveryManager.OnIncomingMsg(ping2);

            // Only one lifecycle manager should have been created (for the first ping)
            INodeLifecycleManager? manager1 = _discoveryManager.GetNodeLifecycleManager(new Node(TestItem.PublicKeyC, "203.0.113.50", Port));
            INodeLifecycleManager? manager2 = _discoveryManager.GetNodeLifecycleManager(new Node(TestItem.PublicKeyD, "203.0.113.50", Port));

            Assert.That(manager1, Is.Not.Null, "First node from the IP should be accepted");
            Assert.That(manager2, Is.Null, "Second node from the same IP should be filtered");
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
            await manager!.SendFindNode(_nodeTable.MasterNode!.Id.Bytes);
            await _msgSender.Received(1).SendMsg(Arg.Is<FindNodeMsg>(m => m.FarAddress!.Address.ToString() == Host && m.FarAddress.Port == Port));

            //receiving findNode
            NeighborsMsg msg = new(_publicKey, GetExpirationTime(), _nodes);
            msg.FarAddress = new IPEndPoint(IPAddress.Parse(Host), Port);
            _discoveryManager.OnIncomingMsg(msg);

            //expecting to send 3 pings to both nodes
            Assert.That(() => _msgSender.ReceivedCallsMatching(s => s.SendMsg(Arg.Is<PingMsg>(m => m.FarAddress!.Address.ToString() == _nodes[0].Host && m.FarAddress.Port == _nodes[0].Port)), 3), Is.True.After(600, 10));
            await _msgSender.Received(3).SendMsg(Arg.Is<PingMsg>(m => m.FarAddress!.Address.ToString() == _nodes[1].Host && m.FarAddress.Port == _nodes[1].Port));
        }

        private void ReceiveSomePong()
        {
            PongMsg pongMsg = new(_publicKey, GetExpirationTime(), []);
            pongMsg.FarAddress = new IPEndPoint(IPAddress.Parse(Host), Port);
            _discoveryManager.OnIncomingMsg(pongMsg);
        }

        [Test]
        public async Task SendMessage_DropOldest_WhenQueueIsFull()
        {
            // Use a very slow rate (1/sec) so messages pile up in the queue
            SetupDiscoveryManager(new DiscoveryConfig()
            {
                MaxOutgoingMessagePerSecond = 1
            });

            // Fire 600 fire-and-forget messages (exceeds bounded channel capacity of 512).
            // With DropOldest semantics, the oldest messages are evicted when full,
            // keeping the channel bounded and preventing unbounded task/memory growth.
            for (int i = 0; i < 600; i++)
            {
                FindNodeMsg msg = new(_publicKey, i, []);
                _discoveryManager.SendMessage(msg);
            }

            // Allow the consumer to process a few messages
            await Task.Delay(50);

            // With rate=1/sec, only ~1 message should have been actually sent in 50ms.
            // The key property: the channel is bounded at 512, so ~88 oldest messages were dropped.
            int sent = _msgSender.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IMsgSender.SendMsg));
            Assert.That(sent, Is.LessThan(520), "Bounded channel should prevent unbounded send accumulation");
        }

        [Test]
        [Repeat(10)]
        public async Task RateLimitOutgoingMessage()
        {
            SetupDiscoveryManager(new DiscoveryConfig()
            {
                MaxOutgoingMessagePerSecond = 5
            });

            long startTime = Stopwatch.GetTimestamp();
            FindNodeMsg msg = new(_publicKey, 0, []);
            await _discoveryManager.SendMessageAsync(msg);
            await _discoveryManager.SendMessageAsync(msg);
            await _discoveryManager.SendMessageAsync(msg);
            await _discoveryManager.SendMessageAsync(msg);
            await _discoveryManager.SendMessageAsync(msg);
            await _discoveryManager.SendMessageAsync(msg);
            Stopwatch.GetElapsedTime(startTime).Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(0.9));
        }

        [Test]
        public async Task SendMessage_StartsOnlyOneQueueConsumer_WhenFirstUseIsConcurrent()
        {
            SetupDiscoveryManager(new DiscoveryConfig
            {
                MaxOutgoingMessagePerSecond = 1000
            });

            const int concurrency = 16;
            Task[] sendTasks = new Task[concurrency];
            ManualResetEventSlim start = new(false);

            for (int i = 0; i < concurrency; i++)
            {
                int expiration = i;
                sendTasks[i] = Task.Run(() =>
                {
                    start.Wait();
                    _discoveryManager.SendMessage(new FindNodeMsg(_publicKey, expiration, []));
                });
            }

            start.Set();
            await Task.WhenAll(sendTasks);

            ((DiscoveryManager)_discoveryManager).SendQueueConsumersCreated.Should().Be(1);
        }
    }

    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class DiscoveryAppTests
    {
        private const string TestPrivateKeyHex = "0x3a1076bf45ab87712ad64ccb3b10217737f7faacbf2872e88fdd9a537d8fe266";

        [Test]
        public async Task StopAsync_ShouldIgnoreDiscoveredNodesAfterStop()
        {
            PrivateKey privateKey = new(TestPrivateKeyHex);
            IProtectedPrivateKey nodeKey = new InsecureProtectedPrivateKey(privateKey);
            INodesLocator nodesLocator = Substitute.For<INodesLocator>();
            IDiscoveryManager discoveryManager = Substitute.For<IDiscoveryManager>();
            INodeTable nodeTable = Substitute.For<INodeTable>();
            IMessageSerializationService messageSerializationService = Substitute.For<IMessageSerializationService>();
            ICryptoRandom cryptoRandom = Substitute.For<ICryptoRandom>();
            INetworkStorage discoveryStorage = Substitute.For<INetworkStorage>();
            IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();
            processExitSource.Token.Returns(CancellationToken.None);

            Node masterNode = new(privateKey.PublicKey, IPAddress.Loopback.ToString(), 30303);
            nodeTable.MasterNode.Returns(masterNode);

            DiscoveryConfig discoveryConfig = new();
            NetworkConfig networkConfig = new();
            ILogManager logManager = LimboLogs.Instance;
            DiscoveryPersistenceManager persistenceManager = new(discoveryStorage, discoveryManager, discoveryConfig, logManager);

            DiscoveryApp discoveryApp = new(
                nodeKey,
                nodesLocator,
                discoveryManager,
                nodeTable,
                messageSerializationService,
                cryptoRandom,
                discoveryStorage,
                persistenceManager,
                processExitSource,
                networkConfig,
                discoveryConfig,
                Timestamper.Default,
                logManager);

            int addedNodes = 0;
            discoveryApp.NodeAdded += (_, _) => addedNodes++;

            Node discoveredNode = new(TestItem.PublicKeyA, "192.168.10.5", 30303);
            discoveryManager.NodeDiscovered += Raise.EventWith(new NodeEventArgs(discoveredNode));
            Assert.That(addedNodes, Is.EqualTo(1));

            await discoveryApp.StopAsync();

            discoveryManager.NodeDiscovered += Raise.EventWith(new NodeEventArgs(discoveredNode));
            Assert.That(addedNodes, Is.EqualTo(1));
        }
    }
}
