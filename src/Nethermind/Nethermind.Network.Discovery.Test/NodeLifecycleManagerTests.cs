// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using MathNet.Numerics.Random;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
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

namespace Nethermind.Network.Discovery.Test;

[Parallelizable(ParallelScope.Self)]
public class NodeLifecycleManagerTests
{
    private Signature[] _signatureMocks = [];
    private PublicKey[] _nodeIds = [];
    private INodeStats _nodeStatsMock = null!;

    private readonly INetworkConfig _networkConfig = new NetworkConfig();
    private IDiscoveryManager _discoveryManager = null!;
    private IDiscoveryManager _discoveryManagerMock = null!;
    private IDiscoveryConfig _discoveryConfigMock = null!;
    private INodeTable _nodeTable = null!;
    private IEvictionManager _evictionManagerMock = null!;
    private ILogger _loggerMock = default;
    private readonly int _port = 1;
    private readonly string _host = "192.168.1.27";

    [SetUp]
    public void Setup()
    {
        _discoveryManagerMock = Substitute.For<IDiscoveryManager>();
        _discoveryConfigMock = Substitute.For<IDiscoveryConfig>();

        NetworkNodeDecoder.Init();
        SetupNodeIds();

        LimboLogs? logManager = LimboLogs.Instance;
        _loggerMock = new(Substitute.For<InterfaceLogger>());
        //setting config to store 3 nodes in a bucket and for table to have one bucket//setting config to store 3 nodes in a bucket and for table to have one bucket

        IConfigProvider configurationProvider = new ConfigProvider();
        _networkConfig.ExternalIp = "99.10.10.66";
        _networkConfig.LocalIp = "10.0.0.5";

        IDiscoveryConfig discoveryConfig = configurationProvider.GetConfig<IDiscoveryConfig>();
        discoveryConfig.PongTimeout = 50;
        discoveryConfig.BucketSize = 3;
        discoveryConfig.BucketsCount = 1;

        NodeDistanceCalculator calculator = new(discoveryConfig);

        _nodeTable = new NodeTable(calculator, discoveryConfig, _networkConfig, logManager);
        _nodeTable.Initialize(TestItem.PublicKeyA);
        _nodeStatsMock = Substitute.For<INodeStats>();

        EvictionManager evictionManager = new(_nodeTable, logManager);
        _evictionManagerMock = Substitute.For<IEvictionManager>();
        ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
        NodeLifecycleManagerFactory lifecycleFactory = new(_nodeTable, evictionManager,
            new NodeStatsManager(timerFactory, logManager), new NodeRecord(), discoveryConfig, Timestamper.Default, logManager);

        IMsgSender udpClient = Substitute.For<IMsgSender>();

        SimpleFilePublicKeyDb discoveryDb = new("Test", "test", logManager);
        _discoveryManager = new DiscoveryManager(lifecycleFactory, _nodeTable, new NetworkStorage(discoveryDb, logManager), discoveryConfig, null, logManager);
        _discoveryManager.MsgSender = udpClient;

        _discoveryManagerMock = Substitute.For<IDiscoveryManager>();
        _discoveryManagerMock.NodesFilter.Returns(new NodeFilter(16));
    }

    [Test]
    public async Task sending_ping_receiving_proper_pong_sets_bounded()
    {
        Node node = new(TestItem.PublicKeyB, _host, _port);
        NodeLifecycleManager nodeManager = new(node, _discoveryManagerMock
        , _nodeTable, _evictionManagerMock, _nodeStatsMock, new NodeRecord(), _discoveryConfigMock, Timestamper.Default, _loggerMock);

        byte[] mdc = new byte[32];
        PingMsg? sentPing = null;
        await _discoveryManagerMock.SendMessageAsync(Arg.Do<PingMsg>(msg =>
        {
            msg.Mdc = mdc;
            sentPing = msg;
        }));

        await nodeManager.SendPingAsync();
        nodeManager.ProcessPongMsg(new PongMsg(node.Address, GetExpirationTime(), sentPing!.Mdc!));

        Assert.That(nodeManager.IsBonded, Is.True);
    }

    [Test]
    public async Task handling_findnode_msg_will_limit_result_to_12()
    {
        IDiscoveryConfig discoveryConfig = new DiscoveryConfig();
        discoveryConfig.PongTimeout = 50;
        discoveryConfig.BucketSize = 32;
        discoveryConfig.BucketsCount = 1;

        _nodeTable = new NodeTable(new NodeDistanceCalculator(discoveryConfig), discoveryConfig, _networkConfig, LimboLogs.Instance);
        _nodeTable.Initialize(TestItem.PublicKeyA);

        Node node = new(TestItem.PublicKeyB, _host, _port);
        NodeLifecycleManager nodeManager = new(node, _discoveryManagerMock, _nodeTable, _evictionManagerMock, _nodeStatsMock, new NodeRecord(), _discoveryConfigMock, Timestamper.Default, _loggerMock);

        await BondWithSelf(nodeManager, node);

        for (int i = 0; i < 32; i++)
        {
            _nodeTable.AddNode(
                new Node(
                    new PublicKey(Random.Shared.NextBytes(64)),
                    "127.0.0.1",
                    i
                ));
        }

        NeighborsMsg? sentMsg = null;
        _discoveryManagerMock.SendMessage(Arg.Do<NeighborsMsg>(msg =>
        {
            sentMsg = msg;
        }));

        nodeManager.ProcessFindNodeMsg(new FindNodeMsg(TestItem.PublicKeyA, 1, new byte[] { 0 }));

        Assert.That(sentMsg, Is.Not.Null);
        _nodeTable.Buckets[0].BondedItemsCount.Should().Be(32);
        sentMsg!.Nodes.Count.Should().Be(12);
    }

    [Test]
    public Task processNeighboursMessage_willCombineTwoSubsequentMessage()
        => processNeighboursMessage_Test((pubkey, i) => new Node(pubkey, $"127.0.0.{i + 1}", 0), 16);

    [Test]
    public Task processNeighboursMessage_willCombineDeduplicateMultipleIps()
        => processNeighboursMessage_Test((pubkey, i) => new Node(pubkey, $"127.0.0.100", 0), 1);

    public async Task processNeighboursMessage_Test(Func<PublicKey, int, Node> createNode, int expectedCount)
    {
        IDiscoveryConfig discoveryConfig = new DiscoveryConfig();
        discoveryConfig.PongTimeout = 50;
        discoveryConfig.BucketSize = 32;
        discoveryConfig.BucketsCount = 1;

        _nodeTable = new NodeTable(new NodeDistanceCalculator(discoveryConfig), discoveryConfig, _networkConfig, LimboLogs.Instance);
        _nodeTable.Initialize(TestItem.PublicKeyA);

        Node node = new(TestItem.PublicKeyB, _host, _port);
        NodeLifecycleManager nodeManager = new(node, _discoveryManagerMock, _nodeTable, _evictionManagerMock, _nodeStatsMock, new NodeRecord(), _discoveryConfigMock, Timestamper.Default, _loggerMock);

        await BondWithSelf(nodeManager, node);

        _discoveryManagerMock
            .Received(0)
            .GetNodeLifecycleManager(Arg.Any<Node>(), Arg.Any<bool>());

        await nodeManager.SendFindNode([]);

        Node[] firstNodes = TestItem.PublicKeys
            .Take(12)
            .Select(createNode)
            .ToArray();
        NeighborsMsg firstNodeMsg = new NeighborsMsg(TestItem.PublicKeyA, 1, firstNodes);
        Node[] secondNodes = TestItem.PublicKeys
            .Skip(12)
            .Take(4)
            .Select((pubkey, i) => createNode(pubkey, i + 14))
            .ToArray();
        NeighborsMsg secondNodeMsg = new NeighborsMsg(TestItem.PublicKeyA, 1, secondNodes);

        nodeManager.ProcessNeighborsMsg(firstNodeMsg);
        nodeManager.ProcessNeighborsMsg(secondNodeMsg);

        _discoveryManagerMock
            .Received(expectedCount)
            .GetNodeLifecycleManager(Arg.Any<Node>(), Arg.Any<bool>());
    }

    [Test]
    public async Task sending_ping_receiving_incorrect_pong_does_not_bond()
    {
        Node node = new(TestItem.PublicKeyB, _host, _port);
        NodeLifecycleManager nodeManager = new(node, _discoveryManagerMock
        , _nodeTable, _evictionManagerMock, _nodeStatsMock, new NodeRecord(), _discoveryConfigMock, Timestamper.Default, _loggerMock);

        await nodeManager.SendPingAsync();
        nodeManager.ProcessPongMsg(new PongMsg(TestItem.PublicKeyB, GetExpirationTime(), new byte[] { 1, 1, 1 }));

        Assert.That(nodeManager.IsBonded, Is.False);
    }

    [Test]
    public void Wrong_pong_will_get_ignored()
    {
        Node node = new(TestItem.PublicKeyB, _host, _port);
        INodeLifecycleManager? manager = _discoveryManager.GetNodeLifecycleManager(node);
        Assert.That(manager?.State, Is.EqualTo(NodeLifecycleState.New));

        PongMsg msgI = new(_nodeIds[0], GetExpirationTime(), new byte[32]);
        msgI.FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port);
        _discoveryManager.OnIncomingMsg(msgI);

        Assert.That(manager?.State, Is.EqualTo(NodeLifecycleState.New));
    }

    [Test]
    [Retry(3)]
    public async Task UnreachableStateTest()
    {
        Node node = new(TestItem.PublicKeyB, _host, _port);
        INodeLifecycleManager? manager = _discoveryManager.GetNodeLifecycleManager(node);
        Assert.That(manager?.State, Is.EqualTo(NodeLifecycleState.New));

        await Task.Delay(500);

        Assert.That(() => manager?.State, Is.EqualTo(NodeLifecycleState.Unreachable).After(500, 50));
        //Assert.AreEqual(NodeLifecycleState.Unreachable, manager.State);
    }

    [Test, Retry(3), Ignore("Eviction changes were introduced and we would need to expose some internals to test bonding")]
    public void EvictCandidateStateWonEvictionTest()
    {
        //adding 3 active nodes
        List<INodeLifecycleManager> managers = new();
        for (int i = 0; i < 3; i++)
        {
            string host = "192.168.1." + i;
            Node node = new(_nodeIds[i], host, _port);
            INodeLifecycleManager? manager = _discoveryManager.GetNodeLifecycleManager(node) ?? throw new Exception("Manager is null");
            managers.Add(manager);
            Assert.That(manager.State, Is.EqualTo(NodeLifecycleState.New));

            PongMsg msgI = new(_nodeIds[i], GetExpirationTime(), new byte[32]);
            msgI.FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port);
            _discoveryManager.OnIncomingMsg(msgI);
            Assert.That(manager.State, Is.EqualTo(NodeLifecycleState.New));
        }

        //table should contain 3 active nodes
        IEnumerable<Node> closestNodes = _nodeTable.GetClosestNodes().ToArray();
        Assert.That(closestNodes.Count(x => x.Host == managers[0].ManagedNode.Host) == 0, Is.True);
        Assert.That(closestNodes.Count(x => x.Host == managers[1].ManagedNode.Host) == 0, Is.True);
        Assert.That(closestNodes.Count(x => x.Host == managers[2].ManagedNode.Host) == 0, Is.True);

        //adding 4th node - table can store only 3, eviction process should start
        Node candidateNode = new(_nodeIds[3], _host, _port);
        INodeLifecycleManager? candidateManager = _discoveryManager.GetNodeLifecycleManager(candidateNode);

        Assert.That(candidateManager?.State, Is.EqualTo(NodeLifecycleState.New));

        PongMsg pongMsg = new(_nodeIds[3], GetExpirationTime(), new byte[32]);
        pongMsg.FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port);
        _discoveryManager.OnIncomingMsg(pongMsg);

        Assert.That(candidateManager?.State, Is.EqualTo(NodeLifecycleState.New));
        INodeLifecycleManager evictionCandidate = managers.First(x => x.State == NodeLifecycleState.EvictCandidate);

        //receiving pong for eviction candidate - should survive
        PongMsg msg = new(evictionCandidate.ManagedNode.Id, GetExpirationTime(), new byte[32]);
        msg.FarAddress = new IPEndPoint(IPAddress.Parse(evictionCandidate.ManagedNode.Host), _port);
        _discoveryManager.OnIncomingMsg(msg);

        //await Task.Delay(100);

        //3th node should survive, 4th node should be active but not in the table
        Assert.That(() => candidateManager?.State, Is.EqualTo(NodeLifecycleState.ActiveExcluded).After(100, 50));
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

    private static long GetExpirationTime() => Timestamper.Default.UnixTime.SecondsLong + 20;

    [Test]
    [Ignore("This test keeps failing and should be only manually enabled / understood when we review the discovery code")]
    public void EvictCandidateStateLostEvictionTest()
    {
        //adding 3 active nodes
        List<INodeLifecycleManager> managers = new();
        for (int i = 0; i < 3; i++)
        {
            string host = "192.168.1." + i;
            Node node = new(_nodeIds[i], host, _port);
            INodeLifecycleManager? manager = _discoveryManager.GetNodeLifecycleManager(node) ?? throw new Exception("Manager is null");
            managers.Add(manager);
            Assert.That(manager.State, Is.EqualTo(NodeLifecycleState.New));

            PongMsg msg = new(_nodeIds[i], GetExpirationTime(), new byte[32]);
            msg.FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port);
            _discoveryManager.OnIncomingMsg(msg);

            Assert.That(manager.State, Is.EqualTo(NodeLifecycleState.Active));
        }

        //table should contain 3 active nodes
        IEnumerable<Node> closestNodes = _nodeTable.GetClosestNodes().ToArray();
        for (int i = 0; i < 3; i++)
        {
            Assert.That(closestNodes.Count(x => x.Host == managers[0].ManagedNode.Host) == 1, Is.True);
        }

        //adding 4th node - table can store only 3, eviction process should start
        Node candidateNode = new(_nodeIds[3], _host, _port);

        INodeLifecycleManager? candidateManager = _discoveryManager.GetNodeLifecycleManager(candidateNode);
        Assert.That(candidateManager?.State, Is.EqualTo(NodeLifecycleState.New));

        PongMsg pongMsg = new(_nodeIds[3], GetExpirationTime(), new byte[32]);
        pongMsg.FarAddress = new IPEndPoint(IPAddress.Parse(_host), _port);
        _discoveryManager.OnIncomingMsg(pongMsg);

        //await Task.Delay(10);
        Assert.That(() => candidateManager?.State, Is.EqualTo(NodeLifecycleState.Active).After(10, 5));
        //Assert.AreEqual(NodeLifecycleState.Active, candidateManager.State);

        INodeLifecycleManager evictionCandidate = managers.First(x => x.State == NodeLifecycleState.EvictCandidate);
        //await Task.Delay(300);

        //3th node should be evicted, 4th node should be added to the table
        //Assert.AreEqual(NodeLifecycleState.Active, candidateManager.State);
        Assert.That(() => candidateManager?.State, Is.EqualTo(NodeLifecycleState.Active).After(300, 50));
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
        }
    }

    private async Task BondWithSelf(NodeLifecycleManager nodeManager, Node node)
    {
        byte[] mdc = new byte[32];
        PingMsg? sentPing = null;
        await _discoveryManagerMock.SendMessageAsync(Arg.Do<PingMsg>(msg =>
        {
            msg.Mdc = mdc;
            sentPing = msg;
        }));
        await nodeManager.SendPingAsync();
        nodeManager.ProcessPongMsg(new PongMsg(node.Address, GetExpirationTime(), sentPing!.Mdc!));
    }

}
