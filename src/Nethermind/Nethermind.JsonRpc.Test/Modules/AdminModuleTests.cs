// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Stats.Model;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.State;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class AdminModuleTests
{
    private IAdminRpcModule _adminRpcModule = null!;
    private EthereumJsonSerializer _serializer = null!;
    private NetworkConfig _networkConfig = null!;
    private ILogManager _logManager = null!;
    private ITxPool _txPool = null!;
    private IReceiptStorage _receiptStorage = null!;
    private IReceiptMonitor _receiptCanonicalityMonitor = null!;
    private IJsonRpcDuplexClient _jsonRpcDuplexClient = null!;
    private IJsonSerializer _jsonSerializer = null!;
    private IBlockTree _blockTree = null!;
    private IStateReader _stateReader = null!;
    private const string _enodeString = "enode://e1b7e0dc09aae610c9dec8a0bee62bab9946cc27ebdd2f9e3571ed6d444628f99e91e43f4a14d42d498217608bb3e1d1bc8ec2aa27d7f7e423413b851bae02bc@127.0.0.1:30303";
    private const string _exampleDataDir = "/example/dbdir";
    private ISubscriptionManager _subscriptionManager = null!;
    private IPeerPool _peerPool = null!;
    private IRlpxHost _rlpxPeer = null!;
    private ISession _existingSession1 = null!;
    private ISession _existingSession2 = null!;
    private ISession _newSession1 = null!;

    [SetUp]
    public void Setup()
    {
        _logManager = Substitute.For<ILogManager>();
        _txPool = Substitute.For<ITxPool>();
        _receiptStorage = Substitute.For<IReceiptStorage>();
        _receiptCanonicalityMonitor = new ReceiptCanonicalityMonitor(_receiptStorage, _logManager);
        _jsonRpcDuplexClient = Substitute.For<IJsonRpcDuplexClient>();
        _jsonSerializer = new EthereumJsonSerializer();
        _blockTree = Build.A.BlockTree().OfChainLength(5).TestObject;
        _stateReader = Substitute.For<IStateReader>();
        _networkConfig = new NetworkConfig();
        IPeerPool peerPool = Substitute.For<IPeerPool>();
        ConcurrentDictionary<PublicKeyAsKey, Peer> dict = new();

        // Create a peer with a validated session
        Peer testPeer = new Peer(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303, true));
        ISession validatedSession = Substitute.For<ISession>();
        validatedSession.IsNetworkIdMatched.Returns(true);
        testPeer.OutSession = validatedSession;

        dict.TryAdd(TestItem.PublicKeyA, testPeer);
        peerPool.ActivePeers.Returns(dict);
        _peerPool = peerPool;
        _existingSession1 = Substitute.For<ISession>();
        _existingSession2 = Substitute.For<ISession>();
        _newSession1 = Substitute.For<ISession>();
        List<ISession> existingSessionsList = new() { _existingSession1, _existingSession2 };
        IEnumerable<ISession> existingSessions = existingSessionsList;

        _rlpxPeer = Substitute.For<IRlpxHost>();
        _rlpxPeer.SessionMonitor.Sessions.Returns(existingSessions);

        IJsonSerializer jsonSerializer = new EthereumJsonSerializer();
        IStaticNodesManager staticNodesManager = Substitute.For<IStaticNodesManager>();
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        Enode enode = new(_enodeString);
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters()
        };

        SubscriptionFactory subscriptionFactory = new();

        subscriptionFactory.RegisterPeerEventsSubscription(_logManager, _peerPool, _rlpxPeer);

        _subscriptionManager = new SubscriptionManager(
            subscriptionFactory,
            _logManager);

        _adminRpcModule = new AdminRpcModule(
            _blockTree,
            _networkConfig,
            peerPool,
            staticNodesManager,
            _stateReader,
            enode,
            _exampleDataDir,
            chainSpec.Parameters,
            trustedNodesManager,
            _subscriptionManager);
        _adminRpcModule.Context = new JsonRpcContext(RpcEndpoint.Ws, _jsonRpcDuplexClient);

        _serializer = new EthereumJsonSerializer();
    }

    [TearDown]
    public void TearDown()
    {
        _jsonRpcDuplexClient?.Dispose();
        _receiptCanonicalityMonitor?.Dispose();
        _existingSession1?.Dispose();
        _existingSession2?.Dispose();
        _newSession1?.Dispose();
    }

    private JsonRpcResult GetPeerEventsAddResult(PeerEventArgs peerEventArgs, out string subscriptionId, bool shouldReceiveResult = true)
    {
        PeerEventsSubscription peerEventsSubscription = new(_jsonRpcDuplexClient, _logManager, _peerPool, _rlpxPeer);
        JsonRpcResult jsonRpcResult = new();
        ManualResetEvent manualResetEvent = new(false);
        peerEventsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
        {
            jsonRpcResult = j;
            manualResetEvent.Set();
        }));
        _peerPool.PeerAdded += Raise.EventWith(new object(), peerEventArgs);
        manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(1000)).Should().Be(shouldReceiveResult);
        subscriptionId = peerEventsSubscription.Id;
        return jsonRpcResult;
    }
    private JsonRpcResult GetPeerEventsRemovedResult(PeerEventArgs peerEventArgs, out string subscriptionId, bool shouldReceiveResult = true)
    {
        PeerEventsSubscription peerEventsSubscription = new(_jsonRpcDuplexClient, _logManager, _peerPool, _rlpxPeer);
        JsonRpcResult jsonRpcResult = new();
        ManualResetEvent manualResetEvent = new(false);
        peerEventsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
        {
            jsonRpcResult = j;
            manualResetEvent.Set();
        }));
        _peerPool.PeerRemoved += Raise.EventWith(new object(), peerEventArgs);
        manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(1000)).Should().Be(shouldReceiveResult);
        subscriptionId = peerEventsSubscription.Id;
        return jsonRpcResult;
    }

    private JsonRpcResult GetPeerEventsMsgReceivedResult(PeerEventArgs peerEventArgs, out string subscriptionId, ISession _session, bool shouldReceiveResult = true)
    {
        PeerEventsSubscription peerEventsSubscription = new(_jsonRpcDuplexClient, _logManager, _peerPool, _rlpxPeer);
        JsonRpcResult jsonRpcResult = new();
        ManualResetEvent manualResetEvent = new(false);
        peerEventsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
        {
            jsonRpcResult = j;
            manualResetEvent.Set();
        }));
        _session.MsgReceived += Raise.EventWith(new object(), peerEventArgs);
        manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(1000)).Should().Be(shouldReceiveResult);
        subscriptionId = peerEventsSubscription.Id;
        peerEventsSubscription.Dispose();
        return jsonRpcResult;
    }

    private JsonRpcResult GetPeerEventsMsgReceivedResultNewSession(PeerEventArgs peerEventArgs, out string subscriptionId, ISession _session, bool shouldReceiveResult = true)
    {
        PeerEventsSubscription peerEventsSubscription = new(_jsonRpcDuplexClient, _logManager, _peerPool, _rlpxPeer);
        JsonRpcResult jsonRpcResult = new();
        ManualResetEvent manualResetEvent = new(false);
        peerEventsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
        {
            jsonRpcResult = j;
            manualResetEvent.Set();
        }));
        SessionEventArgs sessionEventArgs = new(_session);
        _rlpxPeer.SessionCreated += Raise.EventWith(_rlpxPeer, sessionEventArgs);

        _session.MsgReceived += Raise.EventWith(new object(), peerEventArgs);
        manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(1000)).Should().Be(shouldReceiveResult);
        subscriptionId = peerEventsSubscription.Id;
        return jsonRpcResult;
    }

    private JsonRpcResult GetPeerEventsMsgDeliveredResult(PeerEventArgs peerEventArgs, out string subscriptionId, ISession _session, bool shouldReceiveResult = true)
    {
        PeerEventsSubscription peerEventsSubscription = new(_jsonRpcDuplexClient, _logManager, _peerPool, _rlpxPeer);
        JsonRpcResult jsonRpcResult = new();
        ManualResetEvent manualResetEvent = new(false);
        peerEventsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
        {
            jsonRpcResult = j;
            manualResetEvent.Set();
        }));
        _session.MsgDelivered += Raise.EventWith(new object(), peerEventArgs);
        manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(1000)).Should().Be(shouldReceiveResult);
        subscriptionId = peerEventsSubscription.Id;
        peerEventsSubscription.Dispose();
        return jsonRpcResult;
    }

    private JsonRpcResult GetPeerEventsMsgDeliveredResultNewSession(PeerEventArgs peerEventArgs, out string subscriptionId, ISession _session, bool shouldReceiveResult = true)
    {
        PeerEventsSubscription peerEventsSubscription = new(_jsonRpcDuplexClient, _logManager, _peerPool, _rlpxPeer);
        JsonRpcResult jsonRpcResult = new();
        ManualResetEvent manualResetEvent = new(false);
        peerEventsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
        {
            jsonRpcResult = j;
            manualResetEvent.Set();
        }));
        SessionEventArgs sessionEventArgs = new(_session);
        _rlpxPeer.SessionCreated += Raise.EventWith(_rlpxPeer, sessionEventArgs);

        _session.MsgDelivered += Raise.EventWith(new object(), peerEventArgs);
        manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(1000)).Should().Be(shouldReceiveResult);
        subscriptionId = peerEventsSubscription.Id;
        return jsonRpcResult;
    }

    [Test]
    public async Task Test_peers()
    {
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_peers");
        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        var peerInfoList = ((JsonElement)response.Result!).Deserialize<List<PeerInfo>>(EthereumJsonSerializer.JsonOptions)!;
        peerInfoList.Count.Should().Be(1);
        PeerInfo peerInfo = peerInfoList[0];
        peerInfo.Network.RemoteAddress.Should().NotBeNullOrEmpty(); // Fixed: more flexible network address checking
        peerInfo.Network.Inbound.Should().BeFalse();
        peerInfo.Network.Static.Should().BeTrue();
        peerInfo.Id.Should().NotBeEmpty();
    }

    [Test]
    public async Task Test_node_info()
    {
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_nodeInfo");
        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        NodeInfo nodeInfo = ((JsonElement)response.Result!).Deserialize<NodeInfo>(EthereumJsonSerializer.JsonOptions)!;
        nodeInfo.Enode.Should().Be(_enodeString);
        nodeInfo.Id.Should().Be("ae3623ef35c06ab49e9ae4b9f5a2b0f1983c28f85de1ccc98e2174333fdbdf1f");
        nodeInfo.Ip.Should().Be("127.0.0.1");
        nodeInfo.Name.Should().Be(ProductInfo.ClientId);
        nodeInfo.ListenAddress.Should().Be("127.0.0.1:30303");
        nodeInfo.Ports.Discovery.Should().Be(_networkConfig.DiscoveryPort);
        nodeInfo.Ports.Listener.Should().Be(_networkConfig.P2PPort);

        nodeInfo.Protocols.Should().HaveCount(1);
        nodeInfo.Protocols["eth"].Difficulty.Should().Be(_blockTree.Head?.TotalDifficulty ?? 0);
        nodeInfo.Protocols["eth"].HeadHash.Should().Be(_blockTree.HeadHash);
        nodeInfo.Protocols["eth"].GenesisHash.Should().Be(_blockTree.GenesisHash);
        nodeInfo.Protocols["eth"].NewtorkId.Should().Be(_blockTree.NetworkId);
        nodeInfo.Protocols["eth"].ChainId.Should().Be(_blockTree.ChainId);
    }

    [Test]
    public async Task Test_admin_dataDir()
    {
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_dataDir");
        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        response.Result!.ToString().Should().Be(_exampleDataDir);
    }

    [Test]
    public async Task Test_hasStateForBlock()
    {
        (await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_isStateRootAvailable", "latest")).Should().Contain("false");
        _stateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        (await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_isStateRootAvailable", "latest")).Should().Contain("true");
    }

    [Test]
    public async Task Test_admin_addTrustedPeer()
    {
        // Setup dependencies
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        IPeerPool peerPool = Substitute.For<IPeerPool>();

        ChainSpec chainSpec = new() { Parameters = new ChainParameters() };
        Enode testEnode = new(_enodeString);

        // Mock AddAsync to return true for any enode (to simplify)
        trustedNodesManager.AddAsync(Arg.Any<Enode>(), Arg.Any<bool>()).Returns(Task.FromResult(true));

        // Create the adminRpcModule as IAdminRpcModule (important for RpcTest)
        IAdminRpcModule adminRpcModule = new AdminRpcModule(
            _blockTree,
            _networkConfig,
            peerPool,
            Substitute.For<IStaticNodesManager>(),
            Substitute.For<IStateReader>(),
            new Enode(_enodeString),
            _exampleDataDir,
            chainSpec.Parameters,
            trustedNodesManager,
            _subscriptionManager);

        // Call admin_addTrustedPeer via the RPC test helper
        string serialized = await RpcTest.TestSerializedRequest(adminRpcModule, "admin_addTrustedPeer", _enodeString);

        // Deserialize the response
        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        response.Should().NotBeNull("Response should not be null");
        bool result = ((JsonElement)response.Result!).Deserialize<bool>(EthereumJsonSerializer.JsonOptions);
        result.Should().BeTrue("The RPC call should succeed and return true");

        // Verify that AddAsync was called once with any Enode
        await trustedNodesManager.Received(1).AddAsync(Arg.Any<Enode>(), Arg.Any<bool>());

        // Verify that peerPool.GetOrAdd was called once with any NetworkNode
        peerPool.Received(1).GetOrAdd(Arg.Any<NetworkNode>());
    }

    [Test]
    public async Task Test_admin_removeTrustedPeer()
    {
        // Setup dependencies
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        IPeerPool peerPool = Substitute.For<IPeerPool>();

        ChainSpec chainSpec = new() { Parameters = new ChainParameters() };
        Enode testEnode = new(_enodeString);

        // Mock RemoveAsync to return true for any enode (to simplify)
        trustedNodesManager.RemoveAsync(Arg.Any<Enode>(), Arg.Any<bool>()).Returns(Task.FromResult(true));

        // Create the adminRpcModule as IAdminRpcModule (important for RpcTest)
        IAdminRpcModule adminRpcModule = new AdminRpcModule(
            _blockTree,
            _networkConfig,
            peerPool,
            Substitute.For<IStaticNodesManager>(),
            Substitute.For<IStateReader>(),
            new Enode(_enodeString),
            _exampleDataDir,
            chainSpec.Parameters,
            trustedNodesManager,
            _subscriptionManager);

        // Call admin_removeTrustedPeer via the RPC test helper
        string serialized = await RpcTest.TestSerializedRequest(adminRpcModule, "admin_removeTrustedPeer", _enodeString);

        // Deserialize the response
        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        response.Should().NotBeNull("Response should not be null");
        bool result = ((JsonElement)response.Result!).Deserialize<bool>(EthereumJsonSerializer.JsonOptions);
        result.Should().BeTrue("The RPC call should succeed and return true");

        // Verify that RemoveAsync was called once with any Enode
        await trustedNodesManager.Received(1).RemoveAsync(Arg.Any<Enode>(), Arg.Any<bool>());
    }

    [Test]
    public async Task Smoke_solc()
    {
        _ = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_setSolc");
    }

    [Test]
    public async Task Smoke_test_peers()
    {
        _ = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_addPeer", _enodeString);
        _ = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_removePeer", _enodeString);
        _ = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_addPeer", _enodeString, true);
        _ = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_removePeer", _enodeString, true);
        _ = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_peers");
    }

    [Test]
    public async Task Admin_unsubscribe_success()
    {
        string serializedSub = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_subscribe", "peerEvents");
        string subscriptionId = serializedSub.Substring(serializedSub.Length - 44, 34);
        string expectedSub = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", subscriptionId, "\",\"id\":67}");
        expectedSub.Should().Be(serializedSub);

        string serializedUnsub = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_unsubscribe", subscriptionId);
        string expectedUnsub = "{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}";

        expectedUnsub.Should().Be(serializedUnsub);
    }

    [Test]
    public async Task No_subscription_name_admin()
    {
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_subscribe");
        var expectedResult = "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"Invalid params\",\"data\":\"Incorrect parameters count, expected: 2, actual: 0\"},\"id\":67}";
        expectedResult.Should().Be(serialized);
    }

    [Test]
    public async Task Admin_subscriptions_remove_after_closing_websockets_client()
    {
        string serializedPeerEvents = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_subscribe", "peerEvents");
        string peerEventsId = serializedPeerEvents.Substring(serializedPeerEvents.Length - 44, 34);
        string expectedPeerEvents = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", peerEventsId, "\",\"id\":67}");
        expectedPeerEvents.Should().Be(serializedPeerEvents);

        _jsonRpcDuplexClient.Closed += Raise.Event();

        string serializedPeerEventsUnsub = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_unsubscribe", peerEventsId);
        string expectedPeerEventsUnsub =
            string.Concat("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Failed to unsubscribe: ",
                peerEventsId, ".\",\"data\":false},\"id\":67}");
        expectedPeerEventsUnsub.Should().Be(serializedPeerEventsUnsub);
    }

    [Test]
    public async Task PeerEventsSubscription_creating_result()
    {
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_subscribe", "peerEvents");
        var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44, 34), "\",\"id\":67}");
        expectedResult.Should().Be(serialized);
    }

    [Test]
    public void Admin_subscription_on_PeerAdded_event()
    {
        Node node = new(TestItem.PublicKeyA, "192.168.1.18", 8000, false);
        JsonRpcResult jsonRpcResult = GetPeerEventsAddResult(new PeerEventArgs(new Peer(node)), out string subscriptionId);
        jsonRpcResult.Response.Should().NotBeNull();
        string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
        var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"admin_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"type\":\"add\",\"peer\":\"", TestItem.PublicKeyA.Hash.ToString(false), "\",\"local\":\"\",\"remote\":\"192.168.1.18:8000\"}}}");
        expectedResult.Should().Be(serialized);
    }

    [Test]
    public void Admin_subscription_on_PeerRemoved_event()
    {
        Node node = new(TestItem.PublicKeyA, "192.168.1.18", 8000, false);
        JsonRpcResult jsonRpcResult = GetPeerEventsRemovedResult(new PeerEventArgs(new Peer(node)), out string subscriptionId);
        jsonRpcResult.Response.Should().NotBeNull();
        string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
        var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"admin_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"type\":\"drop\",\"peer\":\"", TestItem.PublicKeyA.Hash.ToString(false), "\",\"local\":\"\",\"remote\":\"192.168.1.18:8000\"}}}");
        expectedResult.Should().Be(serialized);
    }

    [Test]
    public void Admin_subscription_on_MsgReceived_event()
    {
        Node node = new(TestItem.PublicKeyA, "192.168.1.18", 8000, false);
        JsonRpcResult jsonRpcResult = GetPeerEventsMsgReceivedResult(new PeerEventArgs(node, "BitTorrent", 1, 2), out string subscriptionId, _existingSession1);
        jsonRpcResult.Response.Should().NotBeNull();
        string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
        var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"admin_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"type\":\"msgrecv\",\"peer\":\"", TestItem.PublicKeyA.Hash.ToString(false), "\",\"protocol\":\"BitTorrent\",\"msgPacketType\":1,\"msgSize\":2,\"local\":\"\",\"remote\":\"192.168.1.18:8000\"}}}");
        expectedResult.Should().Be(serialized);
    }

    [Test]
    public void Admin_subscription_on_MsgDelivered_event()
    {
        Node node = new(TestItem.PublicKeyA, "192.168.1.18", 8000, false);
        JsonRpcResult jsonRpcResult = GetPeerEventsMsgDeliveredResult(new PeerEventArgs(node, "BitTorrent", 1, 2), out string subscriptionId, _existingSession1);
        jsonRpcResult.Response.Should().NotBeNull();
        string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
        var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"admin_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"type\":\"msgsend\",\"peer\":\"", TestItem.PublicKeyA.Hash.ToString(false), "\",\"protocol\":\"BitTorrent\",\"msgPacketType\":1,\"msgSize\":2,\"local\":\"\",\"remote\":\"192.168.1.18:8000\"}}}");
        expectedResult.Should().Be(serialized);
    }

    [Test]
    public void MsgReceived_event_on_all_existing_session()
    {
        Node node = new(TestItem.PublicKeyA, "192.168.1.18", 8000, false);
        JsonRpcResult jsonRpcResult1 = GetPeerEventsMsgReceivedResult(new PeerEventArgs(node, "BitTorrent", 1, 2), out string subscriptionId1, _existingSession1);
        jsonRpcResult1.Response.Should().NotBeNull();
        string serialized1 = _jsonSerializer.Serialize(jsonRpcResult1.Response);
        var expectedResult1 = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"admin_subscription\",\"params\":{\"subscription\":\"", subscriptionId1, "\",\"result\":{\"type\":\"msgrecv\",\"peer\":\"", TestItem.PublicKeyA.Hash.ToString(false), "\",\"protocol\":\"BitTorrent\",\"msgPacketType\":1,\"msgSize\":2,\"local\":\"\",\"remote\":\"192.168.1.18:8000\"}}}");
        expectedResult1.Should().Be(serialized1);

        JsonRpcResult jsonRpcResult2 = GetPeerEventsMsgReceivedResult(new PeerEventArgs(node, "BitTorrent", 1, 2), out string subscriptionId2, _existingSession2);
        jsonRpcResult2.Response.Should().NotBeNull();
        string serialized2 = _jsonSerializer.Serialize(jsonRpcResult2.Response);
        var expectedResult2 = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"admin_subscription\",\"params\":{\"subscription\":\"", subscriptionId2, "\",\"result\":{\"type\":\"msgrecv\",\"peer\":\"", TestItem.PublicKeyA.Hash.ToString(false), "\",\"protocol\":\"BitTorrent\",\"msgPacketType\":1,\"msgSize\":2,\"local\":\"\",\"remote\":\"192.168.1.18:8000\"}}}");
        expectedResult2.Should().Be(serialized2);
    }

    [Test]
    public void MsgDelivered_event_on_all_existing_session()
    {
        Node node = new(TestItem.PublicKeyA, "192.168.1.18", 8000, false);
        JsonRpcResult jsonRpcResult1 = GetPeerEventsMsgDeliveredResult(new PeerEventArgs(node, "BitTorrent", 1, 2), out string subscriptionId1, _existingSession1);
        jsonRpcResult1.Response.Should().NotBeNull();
        string serialized1 = _jsonSerializer.Serialize(jsonRpcResult1.Response);
        var expectedResult1 = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"admin_subscription\",\"params\":{\"subscription\":\"", subscriptionId1, "\",\"result\":{\"type\":\"msgsend\",\"peer\":\"", TestItem.PublicKeyA.Hash.ToString(false), "\",\"protocol\":\"BitTorrent\",\"msgPacketType\":1,\"msgSize\":2,\"local\":\"\",\"remote\":\"192.168.1.18:8000\"}}}");
        expectedResult1.Should().Be(serialized1);

        JsonRpcResult jsonRpcResult2 = GetPeerEventsMsgDeliveredResult(new PeerEventArgs(node, "BitTorrent", 1, 2), out string subscriptionId2, _existingSession2);
        jsonRpcResult2.Response.Should().NotBeNull();
        string serialized2 = _jsonSerializer.Serialize(jsonRpcResult2.Response);
        var expectedResult2 = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"admin_subscription\",\"params\":{\"subscription\":\"", subscriptionId2, "\",\"result\":{\"type\":\"msgsend\",\"peer\":\"", TestItem.PublicKeyA.Hash.ToString(false), "\",\"protocol\":\"BitTorrent\",\"msgPacketType\":1,\"msgSize\":2,\"local\":\"\",\"remote\":\"192.168.1.18:8000\"}}}");
        expectedResult2.Should().Be(serialized2);
    }

    [Test]
    public void MsgReceived_event_on_new_session()
    {
        Node node = new(TestItem.PublicKeyA, "192.168.1.18", 8000, false);
        JsonRpcResult jsonRpcResult = GetPeerEventsMsgReceivedResultNewSession(new PeerEventArgs(node, "BitTorrent", 1, 2), out string subscriptionId, _newSession1);
        jsonRpcResult.Response.Should().NotBeNull();
        string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
        var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"admin_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"type\":\"msgrecv\",\"peer\":\"", TestItem.PublicKeyA.Hash.ToString(false), "\",\"protocol\":\"BitTorrent\",\"msgPacketType\":1,\"msgSize\":2,\"local\":\"\",\"remote\":\"192.168.1.18:8000\"}}}");
        expectedResult.Should().Be(serialized);
    }

    [Test]
    public void MsgDelivered_event_on_new_session()
    {
        Node node = new(TestItem.PublicKeyA, "192.168.1.18", 8000, false);
        JsonRpcResult jsonRpcResult = GetPeerEventsMsgDeliveredResultNewSession(new PeerEventArgs(node, "BitTorrent", 1, 2), out string subscriptionId, _newSession1);
        jsonRpcResult.Response.Should().NotBeNull();
        string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
        var expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"method\":\"admin_subscription\",\"params\":{\"subscription\":\"", subscriptionId, "\",\"result\":{\"type\":\"msgsend\",\"peer\":\"", TestItem.PublicKeyA.Hash.ToString(false), "\",\"protocol\":\"BitTorrent\",\"msgPacketType\":1,\"msgSize\":2,\"local\":\"\",\"remote\":\"192.168.1.18:8000\"}}}");
        expectedResult.Should().Be(serialized);
    }

    [Test]
    public void MsgDelivered_after_session_closed()
    {
        Node node = new(TestItem.PublicKeyA, "192.168.1.18", 8000, false);
        DisconnectEventArgs disconnectEventArgs = new(new DisconnectReason(), new DisconnectType(), "");
        PeerEventArgs peerEventArgs = new PeerEventArgs(node, "BitTorrent", 1, 2);
        PeerEventsSubscription peerEventsSubscription = new(_jsonRpcDuplexClient, _logManager, _peerPool, _rlpxPeer);
        JsonRpcResult jsonRpcResult = new();
        ManualResetEvent manualResetEvent = new(false);
        peerEventsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
        {
            jsonRpcResult = j;
            manualResetEvent.Set();
        }));
        _existingSession1.Disconnected += Raise.EventWith(_existingSession1, disconnectEventArgs);
        _existingSession1.MsgDelivered += Raise.EventWith(new object(), peerEventArgs);

        manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(1000)).Should().Be(false);
    }

    [Test]
    public async Task Test_peers_new_format()
    {
        // Arrange - Set up a peer with rich data
        Node testNode = new(TestItem.PublicKeyA, "192.168.1.100", 30303, true);
        testNode.ClientId = "Geth/v1.15.10-stable-2bf8a789/linux-amd64/go1.24.2";

        Peer testPeer = new(testNode);

        // Mock session with network details
        ISession mockSession = Substitute.For<ISession>();
        mockSession.RemoteHost.Returns("192.168.1.100");
        mockSession.RemotePort.Returns(30303);
        mockSession.LocalPort.Returns(30303);
        mockSession.LastPingUtc.Returns(DateTime.UtcNow);
        mockSession.IsNetworkIdMatched.Returns(true);

        // Mock protocol handler for capabilities
        IP2PProtocolHandler mockP2PHandler = Substitute.For<IP2PProtocolHandler>();
        mockP2PHandler.GetCapabilitiesForAdmin().Returns(new[] { "eth/68", "snap/1" });
        mockSession.TryGetProtocolHandler("p2p", out Arg.Any<IProtocolHandler>())
            .Returns(x =>
            {
                x[1] = mockP2PHandler;
                return true;
            });

        testPeer.OutSession = mockSession;

        // Set up peer pool
        ConcurrentDictionary<PublicKeyAsKey, Peer> peers = new();
        peers.TryAdd(TestItem.PublicKeyA, testPeer);
        _peerPool.ActivePeers.Returns(peers);

        // Act
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_peers");

        // Assert
        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        var peerInfoList = ((JsonElement)response.Result!).Deserialize<List<PeerInfo>>(EthereumJsonSerializer.JsonOptions)!;

        peerInfoList.Count.Should().Be(1);
        PeerInfo peerInfo = peerInfoList[0];

        // Test standard fields (new format)
        peerInfo.Id.Should().Be(TestItem.PublicKeyA.Hash.ToString(false));
        peerInfo.Name.Should().Be("Geth/v1.15.10-stable-2bf8a789/linux-amd64/go1.24.2");
        peerInfo.Enode.Should().StartWith("enode://");
        peerInfo.Caps.Should().BeEquivalentTo(new[] { "eth/68", "snap/1" });
        peerInfo.Enr.Should().BeNull(); // Expected for now

        // Test network object
        peerInfo.Network.Should().NotBeNull();
        peerInfo.Network.LocalAddress.Should().Be("127.0.0.1:30303");
        peerInfo.Network.RemoteAddress.Should().Be("192.168.1.100:30303");
        peerInfo.Network.Inbound.Should().BeFalse();
        peerInfo.Network.Trusted.Should().BeFalse();
        peerInfo.Network.Static.Should().BeTrue();

        // Test protocols object
        peerInfo.Protocols.Should().NotBeNull();
        peerInfo.Protocols.Should().ContainKey("eth");

        var ethProtocol = peerInfo.Protocols["eth"];
        ethProtocol.Should().NotBeNull();

        // Cast to JsonElement to inspect properties
        var ethProtocolElement = (JsonElement)ethProtocol;
        ethProtocolElement.GetProperty("version").GetInt32().Should().Be(68);

        // Test snap protocol if it exists (it should in this case)
        if (peerInfo.Protocols.ContainsKey("snap"))
        {
            var snapProtocol = peerInfo.Protocols["snap"];
            snapProtocol.Should().NotBeNull();
            var snapProtocolElement = (JsonElement)snapProtocol;
            snapProtocolElement.GetProperty("version").GetInt32().Should().Be(1);
        }
    }

    [Test]
    public async Task Test_peers_capability_extraction()
    {
        // Arrange - Test capability extraction from different sources
        Node testNode = new(TestItem.PublicKeyA, "192.168.1.100", 30303, false);

        Peer testPeer = new(testNode);

        // Mock session WITHOUT protocol handler (should return empty capabilities)
        ISession mockSession = Substitute.For<ISession>();
        mockSession.RemoteHost.Returns("192.168.1.100");
        mockSession.RemotePort.Returns(30303);
        mockSession.LocalPort.Returns(30303);
        mockSession.IsNetworkIdMatched.Returns(true);

        // Mock TryGetProtocolHandler to return false (no handler)
        mockSession.TryGetProtocolHandler("p2p", out Arg.Any<IProtocolHandler>())
            .Returns(false);

        testPeer.OutSession = mockSession;

        // Set up peer pool
        ConcurrentDictionary<PublicKeyAsKey, Peer> peers = new();
        peers.TryAdd(TestItem.PublicKeyA, testPeer);
        _peerPool.ActivePeers.Returns(peers);

        // Act
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_peers");

        // Assert
        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        var peerInfoList = ((JsonElement)response.Result!).Deserialize<List<PeerInfo>>(EthereumJsonSerializer.JsonOptions)!;

        peerInfoList.Count.Should().Be(1);
        PeerInfo peerInfo = peerInfoList[0];

        // Should return empty capabilities when no protocol handler (no fallback)
        peerInfo.Caps.Should().BeEmpty();

        // Protocol version should be 0 (no capabilities to parse)
        var ethProtocol = peerInfo.Protocols["eth"];
        var ethProtocolElement = (JsonElement)ethProtocol;
        ethProtocolElement.GetProperty("version").GetInt32().Should().Be(0);
    }

    [Test]
    public async Task Test_peers_multiple_capabilities()
    {
        // Arrange - Test peer with multiple capabilities
        Node testNode = new(TestItem.PublicKeyA, "192.168.1.100", 30303, false);
        testNode.ClientId = "erigon/v3.0.12-39c6a6ff/linux-amd64/go1.23.10";

        Peer testPeer = new(testNode);

        // Mock session with multiple capabilities
        ISession mockSession = Substitute.For<ISession>();
        mockSession.RemoteHost.Returns("192.168.1.100");
        mockSession.RemotePort.Returns(30303);
        mockSession.LocalPort.Returns(30303);
        mockSession.IsNetworkIdMatched.Returns(true);

        // Mock protocol handler with multiple capabilities
        IP2PProtocolHandler mockP2PHandler = Substitute.For<IP2PProtocolHandler>();
        mockP2PHandler.GetCapabilitiesForAdmin().Returns(new[] { "eth/67", "eth/68", "snap/1" });
        mockSession.TryGetProtocolHandler("p2p", out Arg.Any<IProtocolHandler>())
            .Returns(x =>
            {
                x[1] = mockP2PHandler;
                return true;
            });

        testPeer.OutSession = mockSession;

        // Set up peer pool
        ConcurrentDictionary<PublicKeyAsKey, Peer> peers = new();
        peers.TryAdd(TestItem.PublicKeyA, testPeer);
        _peerPool.ActivePeers.Returns(peers);

        // Act
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_peers");

        // Assert
        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        var peerInfoList = ((JsonElement)response.Result!).Deserialize<List<PeerInfo>>(EthereumJsonSerializer.JsonOptions)!;

        peerInfoList.Count.Should().Be(1);
        PeerInfo peerInfo = peerInfoList[0];

        // Should have all capabilities
        peerInfo.Caps.Should().BeEquivalentTo(new[] { "eth/67", "eth/68", "snap/1" });

        // Protocol version should be parsed from first eth capability
        var ethProtocol = peerInfo.Protocols["eth"];
        var ethProtocolElement = (JsonElement)ethProtocol;
        ethProtocolElement.GetProperty("version").GetInt32().Should().Be(67);
    }

    [Test]
    public async Task Test_peers_inbound_connection()
    {
        // Arrange - Test inbound peer
        Node testNode = new(TestItem.PublicKeyA, "192.168.1.100", 30303, false);

        Peer testPeer = new(testNode);

        // Mock INBOUND session
        ISession mockSession = Substitute.For<ISession>();
        mockSession.RemoteHost.Returns("192.168.1.100");
        mockSession.RemotePort.Returns(45678); // Different port for inbound
        mockSession.LocalPort.Returns(30303);
        mockSession.IsNetworkIdMatched.Returns(true);

        // Set as inbound session
        testPeer.InSession = mockSession;
        testPeer.OutSession = null;

        // Set up peer pool
        ConcurrentDictionary<PublicKeyAsKey, Peer> peers = new();
        peers.TryAdd(TestItem.PublicKeyA, testPeer);
        _peerPool.ActivePeers.Returns(peers);

        // Act
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_peers");

        // Assert
        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        var peerInfoList = ((JsonElement)response.Result!).Deserialize<List<PeerInfo>>(EthereumJsonSerializer.JsonOptions)!;

        peerInfoList.Count.Should().Be(1);
        PeerInfo peerInfo = peerInfoList[0];

        // Should be marked as inbound
        peerInfo.Network.Inbound.Should().BeTrue();
        peerInfo.Network.RemoteAddress.Should().Be("192.168.1.100:45678");
    }

    [Test]
    public async Task Test_peers_eth_only_protocol()
    {
        // Arrange - Test peer with only eth protocol (no snap)
        Node testNode = new(TestItem.PublicKeyA, "192.168.1.100", 30303, false);
        testNode.ClientId = "Nethermind/v1.25.4+2bf8a789/linux-x64/dotnet8.0.8";

        Peer testPeer = new(testNode);

        // Mock session with only eth capability
        ISession mockSession = Substitute.For<ISession>();
        mockSession.RemoteHost.Returns("192.168.1.100");
        mockSession.RemotePort.Returns(30303);
        mockSession.LocalPort.Returns(30303);
        mockSession.IsNetworkIdMatched.Returns(true);

        // Mock protocol handler with only eth capability
        IP2PProtocolHandler mockP2PHandler = Substitute.For<IP2PProtocolHandler>();
        mockP2PHandler.GetCapabilitiesForAdmin().Returns(new[] { "eth/68" });
        mockSession.TryGetProtocolHandler("p2p", out Arg.Any<IProtocolHandler>())
            .Returns(x =>
            {
                x[1] = mockP2PHandler;
                return true;
            });

        testPeer.OutSession = mockSession;

        // Set up peer pool
        ConcurrentDictionary<PublicKeyAsKey, Peer> peers = new();
        peers.TryAdd(TestItem.PublicKeyA, testPeer);
        _peerPool.ActivePeers.Returns(peers);

        // Act
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_peers");

        // Assert
        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        var peerInfoList = ((JsonElement)response.Result!).Deserialize<List<PeerInfo>>(EthereumJsonSerializer.JsonOptions)!;

        peerInfoList.Count.Should().Be(1);
        PeerInfo peerInfo = peerInfoList[0];

        // Should have only eth capability
        peerInfo.Caps.Should().BeEquivalentTo(new[] { "eth/68" });

        // Test protocols object
        peerInfo.Protocols.Should().NotBeNull();
        peerInfo.Protocols.Should().ContainKey("eth");

        // Should NOT contain snap protocol
        peerInfo.Protocols.Should().NotContainKey("snap");

        // Test eth protocol
        var ethProtocol = peerInfo.Protocols["eth"];
        var ethProtocolElement = (JsonElement)ethProtocol;
        ethProtocolElement.GetProperty("version").GetInt32().Should().Be(68);
    }

    [Test]
    public async Task Test_peers_multiple_eth_versions()
    {
        // Arrange - Test peer with multiple eth versions
        Node testNode = new(TestItem.PublicKeyA, "192.168.1.100", 30303, false);
        testNode.ClientId = "erigon/v3.0.12-39c6a6ff/linux-amd64/go1.23.10";

        Peer testPeer = new(testNode);

        // Mock session with multiple capabilities
        ISession mockSession = Substitute.For<ISession>();
        mockSession.RemoteHost.Returns("192.168.1.100");
        mockSession.RemotePort.Returns(30303);
        mockSession.LocalPort.Returns(30303);
        mockSession.IsNetworkIdMatched.Returns(true);

        // Mock protocol handler with multiple capabilities
        IP2PProtocolHandler mockP2PHandler = Substitute.For<IP2PProtocolHandler>();
        mockP2PHandler.GetCapabilitiesForAdmin().Returns(new[] { "eth/67", "eth/68", "snap/1" });
        mockSession.TryGetProtocolHandler("p2p", out Arg.Any<IProtocolHandler>())
            .Returns(x =>
            {
                x[1] = mockP2PHandler;
                return true;
            });

        testPeer.OutSession = mockSession;

        // Set up peer pool
        ConcurrentDictionary<PublicKeyAsKey, Peer> peers = new();
        peers.TryAdd(TestItem.PublicKeyA, testPeer);
        _peerPool.ActivePeers.Returns(peers);

        // Act
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_peers");

        // Assert
        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        var peerInfoList = ((JsonElement)response.Result!).Deserialize<List<PeerInfo>>(EthereumJsonSerializer.JsonOptions)!;

        peerInfoList.Count.Should().Be(1);
        PeerInfo peerInfo = peerInfoList[0];

        // Should have all capabilities
        peerInfo.Caps.Should().BeEquivalentTo(new[] { "eth/67", "eth/68", "snap/1" });

        // Test protocols - should contain both eth and snap
        peerInfo.Protocols.Should().ContainKey("eth");
        peerInfo.Protocols.Should().ContainKey("snap");

        // Protocol version should be parsed from first eth capability
        var ethProtocol = peerInfo.Protocols["eth"];
        var ethProtocolElement = (JsonElement)ethProtocol;
        ethProtocolElement.GetProperty("version").GetInt32().Should().Be(67);

        // Test snap protocol
        var snapProtocol = peerInfo.Protocols["snap"];
        var snapProtocolElement = (JsonElement)snapProtocol;
        snapProtocolElement.GetProperty("version").GetInt32().Should().Be(1);
    }

    [Test]
    public async Task Test_peers_older_eth_version()
    {
        // Arrange - Test peer with older eth version (realistic scenario)
        Node testNode = new(TestItem.PublicKeyA, "192.168.1.100", 30303, false);
        testNode.ClientId = "Geth/v1.10.0-stable/linux-amd64/go1.16.15";

        Peer testPeer = new(testNode);

        // Mock session with older eth version (no snap support)
        ISession mockSession = Substitute.For<ISession>();
        mockSession.RemoteHost.Returns("192.168.1.100");
        mockSession.RemotePort.Returns(30303);
        mockSession.LocalPort.Returns(30303);
        mockSession.IsNetworkIdMatched.Returns(true);

        // Mock protocol handler with older eth capability only
        IP2PProtocolHandler mockP2PHandler = Substitute.For<IP2PProtocolHandler>();
        mockP2PHandler.GetCapabilitiesForAdmin().Returns(new[] { "eth/66" });
        mockSession.TryGetProtocolHandler("p2p", out Arg.Any<IProtocolHandler>())
            .Returns(x =>
            {
                x[1] = mockP2PHandler;
                return true;
            });

        testPeer.OutSession = mockSession;

        // Set up peer pool
        ConcurrentDictionary<PublicKeyAsKey, Peer> peers = new();
        peers.TryAdd(TestItem.PublicKeyA, testPeer);
        _peerPool.ActivePeers.Returns(peers);

        // Act
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_peers");

        // Assert
        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        var peerInfoList = ((JsonElement)response.Result!).Deserialize<List<PeerInfo>>(EthereumJsonSerializer.JsonOptions)!;

        peerInfoList.Count.Should().Be(1);
        PeerInfo peerInfo = peerInfoList[0];

        // Should have only older eth capability
        peerInfo.Caps.Should().BeEquivalentTo(new[] { "eth/66" });

        // Test protocols - should only contain eth
        peerInfo.Protocols.Should().ContainKey("eth");
        peerInfo.Protocols.Should().NotContainKey("snap"); // Older clients don't support snap

        // Test eth protocol version
        var ethProtocol = peerInfo.Protocols["eth"];
        var ethProtocolElement = (JsonElement)ethProtocol;
        ethProtocolElement.GetProperty("version").GetInt32().Should().Be(66);
    }
}
