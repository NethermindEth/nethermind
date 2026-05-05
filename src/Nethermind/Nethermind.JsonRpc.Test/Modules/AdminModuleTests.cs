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
using Nethermind.Core.Extensions;
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
    private const string _enodeString = "enode://e1b7e0dc09aae610c9dec8a0bee62bab9946cc27ebdd2f9e3571ed6d444628f99e91e43f4a14d42d498217608bb3e1d1bc8ec2aa27d7f7e423413b851bae02bc@127.0.0.1:30303";
    private const string _exampleDataDir = "/example/dbdir";

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
    private ISubscriptionManager _subscriptionManager = null!;
    private IPeerPool _peerPool = null!;
    private IRlpxHost _rlpxPeer = null!;
    private ISession _existingSession1 = null!;
    private ISession _existingSession2 = null!;

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
        Peer testPeer = new(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303, true));
        ISession validatedSession = Substitute.For<ISession>();
        validatedSession.IsNetworkIdMatched.Returns(true);
        testPeer.OutSession = validatedSession;
        dict.TryAdd(TestItem.PublicKeyA, testPeer);
        peerPool.ActivePeers.Returns(dict);
        _peerPool = peerPool;

        _existingSession1 = Substitute.For<ISession>();
        _existingSession2 = Substitute.For<ISession>();
        List<ISession> existingSessionsList = new() { _existingSession1, _existingSession2 };
        _rlpxPeer = Substitute.For<IRlpxHost>();
        _rlpxPeer.SessionMonitor.Sessions.Returns(existingSessionsList);

        SubscriptionFactory subscriptionFactory = new();
        subscriptionFactory.RegisterPeerEventsSubscription(_logManager, _peerPool, _rlpxPeer);
        _subscriptionManager = new SubscriptionManager(subscriptionFactory, _logManager);

        _adminRpcModule = new AdminRpcModule(
            _blockTree,
            _networkConfig,
            peerPool,
            Substitute.For<IStaticNodesManager>(),
            _stateReader,
            new Enode(_enodeString),
            _exampleDataDir,
            new ChainSpec { Parameters = new ChainParameters() }.Parameters,
            Substitute.For<ITrustedNodesManager>(),
            _subscriptionManager,
            new JsonRpcConfig());
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
    }

    [Test]
    public async Task AdminPeers_WithSingleStaticPeer_ReturnsOnePeerInfo()
    {
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_peers");

        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        List<PeerInfo> peerInfoList = ((JsonElement)response.Result!).Deserialize<List<PeerInfo>>(EthereumJsonSerializer.JsonOptions)!;
        peerInfoList.Count.Should().Be(1, because: "the setup wires exactly one validated active peer");

        PeerInfo peerInfo = peerInfoList[0];
        peerInfo.Network.RemoteAddress.Should().NotBeNullOrEmpty(because: "the validated session must report a remote address");
        peerInfo.Network.Inbound.Should().BeFalse(because: "the test peer is configured with an outbound session");
        peerInfo.Network.Static.Should().BeTrue(because: "the test peer is constructed with isStatic=true");
        peerInfo.Id.Should().NotBeNull(because: "every peer carries a public-key identifier");
    }

    [Test]
    public async Task AdminNodeInfo_OnDefaultModule_ReturnsExpectedNodeAndProtocolFields()
    {
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_nodeInfo");

        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        NodeInfo nodeInfo = ((JsonElement)response.Result!).Deserialize<NodeInfo>(EthereumJsonSerializer.JsonOptions)!;

        nodeInfo.Enode.Should().Be(_enodeString, because: "admin_nodeInfo echoes the configured local enode URL");
        nodeInfo.Id.Should().Be("ae3623ef35c06ab49e9ae4b9f5a2b0f1983c28f85de1ccc98e2174333fdbdf1f", because: "node id is the keccak hash of the configured public key");
        nodeInfo.Ip.Should().Be("127.0.0.1", because: "the configured enode advertises 127.0.0.1");
        nodeInfo.Name.Should().Be(ProductInfo.ClientId, because: "the node identifies itself with the runtime client id");
        nodeInfo.ListenAddress.Should().Be("127.0.0.1:30303", because: "listenAddress is host:port from the configured enode");
        nodeInfo.Ports.Discovery.Should().Be(_networkConfig.DiscoveryPort, because: "discovery port comes from network config");
        nodeInfo.Ports.Listener.Should().Be(_networkConfig.P2PPort, because: "listener port comes from network config");

        nodeInfo.Protocols.Should().HaveCount(1, because: "only the eth protocol is registered in this test setup");
        nodeInfo.Protocols["eth"].Difficulty.Should().Be(_blockTree.Head?.TotalDifficulty ?? 0, because: "difficulty mirrors the head total difficulty");
        nodeInfo.Protocols["eth"].HeadHash.Should().Be(_blockTree.HeadHash, because: "head hash mirrors the block tree head");
        nodeInfo.Protocols["eth"].GenesisHash.Should().Be(_blockTree.GenesisHash, because: "genesis hash mirrors the block tree genesis");
        nodeInfo.Protocols["eth"].NetworkId.Should().Be(_blockTree.NetworkId, because: "network id mirrors the block tree network id");
        nodeInfo.Protocols["eth"].ChainId.Should().Be(_blockTree.ChainId, because: "chain id mirrors the block tree chain id");
    }

    [Test]
    public async Task AdminDataDir_OnDefaultModule_ReturnsConfiguredPath()
    {
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_dataDir");

        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        response.Result!.ToString().Should().Be(_exampleDataDir, because: "admin_dataDir reflects the path passed at module construction");
    }

    [TestCase(false, TestName = "AdminIsStateRootAvailable_WhenStateMissing_ReturnsFalse")]
    [TestCase(true, TestName = "AdminIsStateRootAvailable_WhenStatePresent_ReturnsTrue")]
    public async Task AdminIsStateRootAvailable_ReflectsStateReaderResult(bool stateAvailable)
    {
        _stateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(stateAvailable);

        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_isStateRootAvailable", "latest");

        serialized.Should().Contain(stateAvailable ? "true" : "false", because: "admin_isStateRootAvailable mirrors the state reader's HasStateForBlock answer");
    }

    [TestCase(false, false, TestName = "AdminAddTrustedPeer_WhenPersistentFalse_KeepsAsInMemoryTrustedPeer")]
    [TestCase(true, true, TestName = "AdminAddTrustedPeer_WhenPersistentTrue_AlsoWritesToTrustedNodesFile")]
    public async Task AdminAddTrustedPeer_WithValidEnode_AddsAsTrustedPeerAndReturnsTrue(bool persistent, bool expectedUpdateFile)
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        trustedNodesManager.AddAsync(Arg.Any<Enode>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        IPeerPool peerPool = Substitute.For<IPeerPool>();
        IAdminRpcModule adminRpcModule = BuildAdminRpcModuleWith(peerPool: peerPool, trustedNodesManager: trustedNodesManager);

        string serialized = await RpcTest.TestSerializedRequest(adminRpcModule, "admin_addTrustedPeer", _enodeString, persistent);

        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        bool result = ((JsonElement)response.Result!).Deserialize<bool>(EthereumJsonSerializer.JsonOptions);
        result.Should().BeTrue(because: "a valid enode is added to the trusted peer set and the call must report success as a boolean");
        await trustedNodesManager.Received(1).AddAsync(Arg.Any<Enode>(), expectedUpdateFile, Arg.Any<CancellationToken>());
        peerPool.Received(1).GetOrAdd(Arg.Any<NetworkNode>());
    }

    [Test]
    public async Task AdminAddTrustedPeer_WhenAlreadyTrusted_StillReturnsTrue()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        trustedNodesManager.AddAsync(Arg.Any<Enode>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        IAdminRpcModule adminRpcModule = BuildAdminRpcModuleWith(trustedNodesManager: trustedNodesManager);

        string serialized = await RpcTest.TestSerializedRequest(adminRpcModule, "admin_addTrustedPeer", _enodeString);

        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        bool result = ((JsonElement)response.Result!).Deserialize<bool>(EthereumJsonSerializer.JsonOptions);
        result.Should().BeTrue(because: "addTrustedPeer is idempotent: trusting an already-trusted peer is success, matching geth's Server.AddTrustedPeer semantics");
    }

    [TestCase("admin_addPeer", "not-an-enode", TestName = "AdminAddPeer_WhenEnodeSchemeInvalid_ReturnsInvalidParamsError")]
    [TestCase("admin_addPeer", "enode://badhex@127.0.0.1:30303", TestName = "AdminAddPeer_WhenEnodePublicKeyInvalid_ReturnsInvalidParamsError")]
    [TestCase("admin_removePeer", "not-an-enode", TestName = "AdminRemovePeer_WhenEnodeSchemeInvalid_ReturnsInvalidParamsError")]
    [TestCase("admin_removePeer", "enode://badhex@127.0.0.1:30303", TestName = "AdminRemovePeer_WhenEnodePublicKeyInvalid_ReturnsInvalidParamsError")]
    [TestCase("admin_addTrustedPeer", "not-an-enode", TestName = "AdminAddTrustedPeer_WhenEnodeSchemeInvalid_ReturnsInvalidParamsError")]
    [TestCase("admin_addTrustedPeer", "enode://badhex@127.0.0.1:30303", TestName = "AdminAddTrustedPeer_WhenEnodePublicKeyInvalid_ReturnsInvalidParamsError")]
    [TestCase("admin_removeTrustedPeer", "not-an-enode", TestName = "AdminRemoveTrustedPeer_WhenEnodeSchemeInvalid_ReturnsInvalidParamsError")]
    [TestCase("admin_removeTrustedPeer", "enode://badhex@127.0.0.1:30303", TestName = "AdminRemoveTrustedPeer_WhenEnodePublicKeyInvalid_ReturnsInvalidParamsError")]
    public async Task AdminPeerMethods_WithInvalidEnode_ReturnsInvalidParamsError(string method, string badEnode)
    {
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, method, badEnode);

        serialized.Should().Contain("\"code\":-32602", because: "all four peer-management methods must return InvalidParams whether parsing fails at scheme or content level");
        serialized.Should().Contain("invalid enode", because: "the error message must identify the failing parameter");
    }

    [TestCase(false, false, TestName = "AdminRemoveTrustedPeer_WhenPersistentFalse_DropsInMemoryTrustedEntry")]
    [TestCase(true, true, TestName = "AdminRemoveTrustedPeer_WhenPersistentTrue_AlsoRemovesFromTrustedNodesFile")]
    public async Task AdminRemoveTrustedPeer_WithValidEnode_RemovesFromTrustedAndReturnsTrue(bool persistent, bool expectedUpdateFile)
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        trustedNodesManager.RemoveAsync(Arg.Any<Enode>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        IAdminRpcModule adminRpcModule = BuildAdminRpcModuleWith(trustedNodesManager: trustedNodesManager);

        string serialized = await RpcTest.TestSerializedRequest(adminRpcModule, "admin_removeTrustedPeer", _enodeString, persistent);

        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        bool result = ((JsonElement)response.Result!).Deserialize<bool>(EthereumJsonSerializer.JsonOptions);
        result.Should().BeTrue(because: "a valid enode is removed from the trusted set, reported as a boolean");
        await trustedNodesManager.Received(1).RemoveAsync(Arg.Any<Enode>(), expectedUpdateFile, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AdminRemoveTrustedPeer_WhenPeerNotTrusted_StillReturnsTrue()
    {
        ITrustedNodesManager trustedNodesManager = Substitute.For<ITrustedNodesManager>();
        trustedNodesManager.RemoveAsync(Arg.Any<Enode>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        IAdminRpcModule adminRpcModule = BuildAdminRpcModuleWith(trustedNodesManager: trustedNodesManager);

        string serialized = await RpcTest.TestSerializedRequest(adminRpcModule, "admin_removeTrustedPeer", _enodeString);

        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        bool result = ((JsonElement)response.Result!).Deserialize<bool>(EthereumJsonSerializer.JsonOptions);
        result.Should().BeTrue(because: "removeTrustedPeer is idempotent: untrusting an unknown peer is success, matching geth's Server.RemoveTrustedPeer semantics");
    }

    [Test]
    public async Task AdminSetSolc_WhenInvoked_DoesNotThrow() =>
        _ = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_setSolc");

    [TestCase(false, false, TestName = "AdminAddPeer_WhenPersistentFalse_KeepsAsInMemoryStaticPeer")]
    [TestCase(true, true, TestName = "AdminAddPeer_WhenPersistentTrue_AlsoWritesToStaticNodesFile")]
    public async Task AdminAddPeer_WithValidEnode_AddsAsStaticPeerAndReturnsTrue(bool persistent, bool expectedUpdateFile)
    {
        IStaticNodesManager staticNodesManager = Substitute.For<IStaticNodesManager>();
        staticNodesManager.AddAsync(Arg.Any<NetworkNode>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        IAdminRpcModule adminRpcModule = BuildAdminRpcModuleWith(staticNodesManager: staticNodesManager);

        string serialized = await RpcTest.TestSerializedRequest(adminRpcModule, "admin_addPeer", _enodeString, persistent);

        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        bool result = ((JsonElement)response.Result!).Deserialize<bool>(EthereumJsonSerializer.JsonOptions);
        result.Should().BeTrue(because: "a valid enode is added to the static peer set and the call must report success as a boolean");
        await staticNodesManager.Received(1).AddAsync(Arg.Is<NetworkNode>(n => n.Enode!.Info == _enodeString), expectedUpdateFile, Arg.Any<CancellationToken>());
    }

    [TestCase(false, false, TestName = "AdminRemovePeer_WhenPersistentFalse_DropsInMemoryStaticEntry")]
    [TestCase(true, true, TestName = "AdminRemovePeer_WhenPersistentTrue_AlsoRemovesFromStaticNodesFile")]
    public async Task AdminRemovePeer_WithValidEnode_RemovesFromStaticAndPoolAndReturnsTrue(bool persistent, bool expectedUpdateFile)
    {
        IStaticNodesManager staticNodesManager = Substitute.For<IStaticNodesManager>();
        staticNodesManager.RemoveAsync(Arg.Any<NetworkNode>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        IPeerPool peerPool = Substitute.For<IPeerPool>();
        IAdminRpcModule adminRpcModule = BuildAdminRpcModuleWith(staticNodesManager: staticNodesManager, peerPool: peerPool);

        string serialized = await RpcTest.TestSerializedRequest(adminRpcModule, "admin_removePeer", _enodeString, persistent);

        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        bool result = ((JsonElement)response.Result!).Deserialize<bool>(EthereumJsonSerializer.JsonOptions);
        result.Should().BeTrue(because: "a valid enode is removed from the static peer set and active session, reported as a boolean");
        await staticNodesManager.Received(1).RemoveAsync(Arg.Is<NetworkNode>(n => n.Enode!.Info == _enodeString), expectedUpdateFile, Arg.Any<CancellationToken>());
        peerPool.Received(1).TryRemove(Arg.Any<PublicKey>(), out Arg.Any<Peer>());
    }

    [Test]
    public async Task AdminRemovePeer_WhenPeerNotFound_StillReturnsTrue()
    {
        IStaticNodesManager staticNodesManager = Substitute.For<IStaticNodesManager>();
        staticNodesManager.RemoveAsync(Arg.Any<NetworkNode>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));
        IPeerPool peerPool = Substitute.For<IPeerPool>();
        peerPool.TryRemove(Arg.Any<PublicKey>(), out Arg.Any<Peer>()).Returns(false);
        IAdminRpcModule adminRpcModule = BuildAdminRpcModuleWith(staticNodesManager: staticNodesManager, peerPool: peerPool);

        string serialized = await RpcTest.TestSerializedRequest(adminRpcModule, "admin_removePeer", _enodeString);

        JsonRpcSuccessResponse response = _serializer.Deserialize<JsonRpcSuccessResponse>(serialized);
        bool result = ((JsonElement)response.Result!).Deserialize<bool>(EthereumJsonSerializer.JsonOptions);
        result.Should().BeTrue(because: "removePeer is idempotent: removing an unknown peer is success, matching geth's Server.RemovePeer semantics");
    }

    [Test]
    public async Task AdminUnsubscribe_AfterSubscribe_ReturnsTrue()
    {
        string serializedSub = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_subscribe", "peerEvents");
        string subscriptionId = serializedSub.Substring(serializedSub.Length - 44, 34);
        string expectedSub = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", subscriptionId, "\",\"id\":67}");
        expectedSub.Should().Be(serializedSub, because: "admin_subscribe returns the subscription id in the result envelope");

        string serializedUnsub = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_unsubscribe", subscriptionId);
        string expectedUnsub = "{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}";
        expectedUnsub.Should().Be(serializedUnsub, because: "admin_unsubscribe reports success as boolean true");
    }

    [Test]
    public async Task AdminSubscribe_WithoutName_ReturnsInvalidParams()
    {
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_subscribe");

        string expectedResult = "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"missing value for required argument 0\"},\"id\":67}";
        expectedResult.Should().Be(serialized, because: "missing required argument is a JSON-RPC InvalidParams error");
    }

    [Test]
    public async Task AdminUnsubscribe_AfterClientCloses_ReturnsFailure()
    {
        string serializedPeerEvents = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_subscribe", "peerEvents");
        string peerEventsId = serializedPeerEvents.Substring(serializedPeerEvents.Length - 44, 34);
        string expectedPeerEvents = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", peerEventsId, "\",\"id\":67}");
        expectedPeerEvents.Should().Be(serializedPeerEvents, because: "precondition: subscribe returns the new subscription id");

        _jsonRpcDuplexClient.Closed += Raise.Event();

        string serializedPeerEventsUnsub = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_unsubscribe", peerEventsId);
        string expectedPeerEventsUnsub = string.Concat(
            "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32603,\"message\":\"Failed to unsubscribe: ",
            peerEventsId, ".\",\"data\":false},\"id\":67}");
        expectedPeerEventsUnsub.Should().Be(serializedPeerEventsUnsub, because: "after the client closes, the subscription is removed and unsubscribe fails");
    }

    [Test]
    public async Task AdminSubscribePeerEvents_OnSubscribe_ReturnsSubscriptionId()
    {
        string serialized = await RpcTest.TestSerializedRequest(_adminRpcModule, "admin_subscribe", "peerEvents");

        string expectedResult = string.Concat("{\"jsonrpc\":\"2.0\",\"result\":\"", serialized.Substring(serialized.Length - 44, 34), "\",\"id\":67}");
        expectedResult.Should().Be(serialized, because: "the result envelope wraps the subscription id under the \"result\" key");
    }

    [TestCase(true, "add", TestName = "PeerEvents_OnPeerAdded_NotifiesAddType")]
    [TestCase(false, "drop", TestName = "PeerEvents_OnPeerRemoved_NotifiesDropType")]
    public void PeerEventsSubscription_OnPeerLifecycleEvent_NotifiesExpectedType(bool isAdd, string expectedType)
    {
        Node node = new(TestItem.PublicKeyA, "192.168.1.18", 8000, false);
        PeerEventArgs peerEventArgs = new(new Peer(node));

        JsonRpcResult jsonRpcResult = RaisePeerEventAndCapture(
            isAdd
                ? () => _peerPool.PeerAdded += Raise.EventWith(new object(), peerEventArgs)
                : () => _peerPool.PeerRemoved += Raise.EventWith(new object(), peerEventArgs),
            out string subscriptionId);

        jsonRpcResult.Response.Should().NotBeNull(because: "the subscription must produce a JSON-RPC response when the event fires");
        string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
        string expectedResult = BuildExpectedPeerLifecycleNotification(subscriptionId, expectedType, TestItem.PublicKeyA.Hash.ToString(false));
        expectedResult.Should().Be(serialized, because: $"a peer-{expectedType} event must serialize with type '{expectedType}'");
    }

    [TestCase(true, "msgrecv", TestName = "PeerEvents_OnMsgReceived_NotifiesMsgrecvType")]
    [TestCase(false, "msgsend", TestName = "PeerEvents_OnMsgDelivered_NotifiesMsgsendType")]
    public void PeerEventsSubscription_OnMessageEvent_NotifiesExpectedType(bool isReceived, string expectedType)
    {
        Node node = new(TestItem.PublicKeyA, "192.168.1.18", 8000, false);
        PeerEventArgs peerEventArgs = new(node, "BitTorrent", 1, 2);
        ISession session = _existingSession1;

        JsonRpcResult jsonRpcResult = RaisePeerEventAndCapture(
            isReceived
                ? () => session.MsgReceived += Raise.EventWith(new object(), peerEventArgs)
                : () => session.MsgDelivered += Raise.EventWith(new object(), peerEventArgs),
            out string subscriptionId,
            disposeSubscription: true);

        jsonRpcResult.Response.Should().NotBeNull(because: "the subscription must produce a JSON-RPC response when the event fires");
        string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
        string expectedResult = BuildExpectedMessageNotification(subscriptionId, expectedType, TestItem.PublicKeyA.Hash.ToString(false));
        expectedResult.Should().Be(serialized, because: $"a {expectedType} event must serialize with type '{expectedType}'");
    }

    [TestCase(true, "msgrecv", TestName = "PeerEvents_OnMsgReceivedAcrossExistingSessions_NotifiesEach")]
    [TestCase(false, "msgsend", TestName = "PeerEvents_OnMsgDeliveredAcrossExistingSessions_NotifiesEach")]
    public void PeerEventsSubscription_OnMessageEventAcrossMultipleSessions_NotifiesEach(bool isReceived, string expectedType)
    {
        Node node = new(TestItem.PublicKeyA, "192.168.1.18", 8000, false);
        PeerEventArgs peerEventArgs = new(node, "BitTorrent", 1, 2);
        string peerHash = TestItem.PublicKeyA.Hash.ToString(false);

        JsonRpcResult firstResult = RaisePeerEventAndCapture(
            isReceived
                ? () => _existingSession1.MsgReceived += Raise.EventWith(new object(), peerEventArgs)
                : () => _existingSession1.MsgDelivered += Raise.EventWith(new object(), peerEventArgs),
            out string firstSubscriptionId,
            disposeSubscription: true);
        firstResult.Response.Should().NotBeNull(because: "the subscription must notify on the first existing session");
        BuildExpectedMessageNotification(firstSubscriptionId, expectedType, peerHash)
            .Should().Be(_jsonSerializer.Serialize(firstResult.Response), because: $"first session emits {expectedType}");

        JsonRpcResult secondResult = RaisePeerEventAndCapture(
            isReceived
                ? () => _existingSession2.MsgReceived += Raise.EventWith(new object(), peerEventArgs)
                : () => _existingSession2.MsgDelivered += Raise.EventWith(new object(), peerEventArgs),
            out string secondSubscriptionId,
            disposeSubscription: true);
        secondResult.Response.Should().NotBeNull(because: "the subscription must also notify on the second existing session");
        BuildExpectedMessageNotification(secondSubscriptionId, expectedType, peerHash)
            .Should().Be(_jsonSerializer.Serialize(secondResult.Response), because: $"second session emits {expectedType}");
    }

    [TestCase(true, "msgrecv", TestName = "PeerEvents_OnMsgReceivedFromNewSession_NotifiesNewSessionMsg")]
    [TestCase(false, "msgsend", TestName = "PeerEvents_OnMsgDeliveredFromNewSession_NotifiesNewSessionMsg")]
    public void PeerEventsSubscription_OnMessageEventFromNewSession_NotifiesNewSessionMessage(bool isReceived, string expectedType)
    {
        ISession newSession = Substitute.For<ISession>();
        Node node = new(TestItem.PublicKeyA, "192.168.1.18", 8000, false);
        PeerEventArgs peerEventArgs = new(node, "BitTorrent", 1, 2);

        JsonRpcResult jsonRpcResult = RaisePeerEventAndCapture(
            () =>
            {
                _rlpxPeer.SessionCreated += Raise.EventWith(_rlpxPeer, new SessionEventArgs(newSession));
                if (isReceived)
                {
                    newSession.MsgReceived += Raise.EventWith(new object(), peerEventArgs);
                }
                else
                {
                    newSession.MsgDelivered += Raise.EventWith(new object(), peerEventArgs);
                }
            },
            out string subscriptionId);

        jsonRpcResult.Response.Should().NotBeNull(because: "the subscription must notify on a session created after subscribe");
        string serialized = _jsonSerializer.Serialize(jsonRpcResult.Response);
        string expectedResult = BuildExpectedMessageNotification(subscriptionId, expectedType, TestItem.PublicKeyA.Hash.ToString(false));
        expectedResult.Should().Be(serialized, because: $"a {expectedType} event from the new session must serialize with type '{expectedType}'");
    }

    [Test]
    public void PeerEventsSubscription_OnMsgDeliveredAfterDisconnect_DoesNotNotify()
    {
        Node node = new(TestItem.PublicKeyA, "192.168.1.18", 8000, false);
        DisconnectEventArgs disconnectEventArgs = new(new DisconnectReason(), new DisconnectType(), string.Empty);
        PeerEventArgs peerEventArgs = new(node, "BitTorrent", 1, 2);
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

        manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(1000)).Should().BeFalse(because: "after disconnect the session is unhooked and must not raise further notifications");
    }

    [Test]
    public void AdminPeers_WithGethStaticPeerExposingSnap_ReturnsBothCapabilities()
    {
        Capability[] capabilities = [new Capability("eth", 68), new Capability("snap", 1)];
        Peer peer = CreateTestPeer("Geth/v1.15.10-stable-2bf8a789/linux-amd64/go1.24.2", capabilities, isStatic: true);
        AdminRpcModule module = CreateMinimalAdminModule(CreatePeerPool(peer));

        ResultWrapper<PeerInfo[]> result = module.admin_peers();

        result.Data.Should().HaveCount(1, because: "the peer pool was seeded with exactly one peer");
        PeerInfo peerInfo = result.Data[0];
        peerInfo.Id.Should().Be(TestItem.PublicKeyA, because: "the peer id is the public key passed to CreateTestPeer");
        peerInfo.Name.Should().Be("Geth/v1.15.10-stable-2bf8a789/linux-amd64/go1.24.2", because: "the peer's client id is reported under name");
        peerInfo.Network.Static.Should().BeTrue(because: "isStatic was set to true on the test peer");
        peerInfo.Network.Inbound.Should().BeFalse(because: "the test peer was set up with an outbound session");
        peerInfo.Caps.Should().BeEquivalentTo(capabilities, because: "all advertised capabilities must surface in admin_peers");
    }

    [Test]
    public void AdminPeers_WhenPeerHasNoCapabilities_ReturnsEmptyCaps()
    {
        Peer peer = CreateTestPeer("TestClient", []);
        AdminRpcModule module = CreateMinimalAdminModule(CreatePeerPool(peer));

        ResultWrapper<PeerInfo[]> result = module.admin_peers();

        result.Data[0].Caps.Should().BeEmpty(because: "a peer with no capabilities must report an empty caps array");
    }

    [Test]
    public void AdminPeers_WithMultipleEthVersionsAndSnap_ReturnsAllProtocols()
    {
        Capability[] capabilities = [new Capability("eth", 67), new Capability("eth", 68), new Capability("snap", 1)];
        Peer peer = CreateTestPeer("erigon/v3.0.12-39c6a6ff/linux-amd64/go1.23.10", capabilities);
        AdminRpcModule module = CreateMinimalAdminModule(CreatePeerPool(peer));

        ResultWrapper<PeerInfo[]> result = module.admin_peers();

        PeerInfo peerInfo = result.Data[0];
        peerInfo.Caps.Should().BeEquivalentTo(capabilities, because: "all advertised eth versions plus snap must be reported");
        peerInfo.Protocols.Should().ContainKeys(new[] { "eth", "snap" }, because: "both eth and snap protocols are present in the capabilities");
    }

    [Test]
    public void AdminPeers_OnInboundPeer_MarksNetworkInboundTrue()
    {
        Peer peer = CreateTestPeer("TestClient", [], isInbound: true);
        AdminRpcModule module = CreateMinimalAdminModule(CreatePeerPool(peer));

        ResultWrapper<PeerInfo[]> result = module.admin_peers();

        PeerInfo peerInfo = result.Data[0];
        peerInfo.Network.Inbound.Should().BeTrue(because: "the peer was created with isInbound=true");
        peerInfo.Network.RemoteAddress.Should().Be("192.168.1.100:45678", because: "inbound sessions report the originating remote address");
    }

    [TestCase(68, "Nethermind/v1.25.4+2bf8a789/linux-x64/dotnet8.0.8", TestName = "AdminPeers_WithEthOnlyV68Peer_DoesNotIncludeSnap")]
    [TestCase(66, "Geth/v1.10.0-stable/linux-amd64/go1.16.15", TestName = "AdminPeers_WithLegacyEthV66Peer_DoesNotIncludeSnap")]
    public void AdminPeers_WithEthOnlyPeer_DoesNotIncludeSnapProtocol(int ethVersion, string clientId)
    {
        Capability[] capabilities = [new Capability("eth", ethVersion)];
        Peer peer = CreateTestPeer(clientId, capabilities);
        AdminRpcModule module = CreateMinimalAdminModule(CreatePeerPool(peer));

        ResultWrapper<PeerInfo[]> result = module.admin_peers();

        PeerInfo peerInfo = result.Data[0];
        peerInfo.Caps.Should().BeEquivalentTo(capabilities, because: "only the advertised eth capability must surface");
        peerInfo.Protocols.Should().ContainKey("eth", because: "the eth protocol entry must be present");
        peerInfo.Protocols.Should().NotContainKey("snap", because: "snap was not advertised by this peer");
    }

    [Test]
    public void PeerInfo_WithHashedPublicKeyJson_DeserializesSuccessfully()
    {
        const string fullKeyHex = "a49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365f52e5a8728693008d97ae83d51194f273455acf1a30e6f3926aefaede484c07d8ec3";
        byte[] fullPublicKeyBytes = Bytes.FromHexString(fullKeyHex);
        byte[] expectedHashBytes = Keccak.Compute(fullPublicKeyBytes).Bytes.ToArray();
        string expectedHashHex = Convert.ToHexString(expectedHashBytes).ToLower();
        string json = $$"""
            {
                "id": "0x{{expectedHashHex}}",
                "name": "test-peer",
                "enode": "enode://{{fullKeyHex}}@127.0.0.1:30303",
                "caps": [],
                "network": { "localAddress": "127.0.0.1", "remoteAddress": "127.0.0.1:30303" },
                "protocols": { "eth": { "version": 0 } }
            }
            """;
        EthereumJsonSerializer serializer = new();

        PeerInfo peerInfo = serializer.Deserialize<PeerInfo>(json);

        peerInfo.Id.Should().NotBeNull(because: "a hashed-id PeerInfo JSON must still produce a non-null id");
        peerInfo.Id.Bytes.Length.Should().Be(64, because: "the public key payload retains its 64-byte length even when the JSON only carried the 32-byte hash");
        peerInfo.Id.Bytes.AsSpan(32, 32).ToArray().Should().BeEquivalentTo(expectedHashBytes, because: "the hash bytes from the JSON must occupy the last 32 bytes of the public key payload");
    }

    private IAdminRpcModule BuildAdminRpcModuleWith(
        IStaticNodesManager? staticNodesManager = null,
        ITrustedNodesManager? trustedNodesManager = null,
        IPeerPool? peerPool = null)
    {
        ChainSpec chainSpec = new() { Parameters = new ChainParameters() };
        return new AdminRpcModule(
            _blockTree,
            _networkConfig,
            peerPool ?? Substitute.For<IPeerPool>(),
            staticNodesManager ?? Substitute.For<IStaticNodesManager>(),
            Substitute.For<IStateReader>(),
            new Enode(_enodeString),
            _exampleDataDir,
            chainSpec.Parameters,
            trustedNodesManager ?? Substitute.For<ITrustedNodesManager>(),
            _subscriptionManager,
            new JsonRpcConfig());
    }

    private JsonRpcResult RaisePeerEventAndCapture(Action raiseEvent, out string subscriptionId, bool disposeSubscription = false, bool shouldReceive = true)
    {
        PeerEventsSubscription peerEventsSubscription = new(_jsonRpcDuplexClient, _logManager, _peerPool, _rlpxPeer);
        JsonRpcResult jsonRpcResult = new();
        ManualResetEvent manualResetEvent = new(false);
        peerEventsSubscription.JsonRpcDuplexClient.SendJsonRpcResult(Arg.Do<JsonRpcResult>(j =>
        {
            jsonRpcResult = j;
            manualResetEvent.Set();
        }));

        raiseEvent();

        manualResetEvent.WaitOne(TimeSpan.FromMilliseconds(1000)).Should().Be(shouldReceive, because: "the subscription should fire within the timeout");
        subscriptionId = peerEventsSubscription.Id;
        if (disposeSubscription)
        {
            peerEventsSubscription.Dispose();
        }
        return jsonRpcResult;
    }

    private static string BuildExpectedPeerLifecycleNotification(string subscriptionId, string type, string peerHash) =>
        string.Concat(
            "{\"jsonrpc\":\"2.0\",\"method\":\"admin_subscription\",\"params\":{\"subscription\":\"",
            subscriptionId,
            "\",\"result\":{\"type\":\"",
            type,
            "\",\"peer\":\"",
            peerHash,
            "\",\"local\":\"192.168.1.18\",\"remote\":\"192.168.1.18:8000\"}}}");

    private static string BuildExpectedMessageNotification(string subscriptionId, string type, string peerHash) =>
        string.Concat(
            "{\"jsonrpc\":\"2.0\",\"method\":\"admin_subscription\",\"params\":{\"subscription\":\"",
            subscriptionId,
            "\",\"result\":{\"type\":\"",
            type,
            "\",\"peer\":\"",
            peerHash,
            "\",\"protocol\":\"BitTorrent\",\"msgPacketType\":1,\"msgSize\":2,\"local\":\"192.168.1.18\",\"remote\":\"192.168.1.18:8000\"}}}");

    private static AdminRpcModule CreateMinimalAdminModule(IPeerPool peerPool)
    {
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(1).TestObject;
        NetworkConfig networkConfig = new();
        IStateReader stateReader = Substitute.For<IStateReader>();
        ISubscriptionManager subscriptionManager = Substitute.For<ISubscriptionManager>();
        return new AdminRpcModule(
            blockTree,
            networkConfig,
            peerPool,
            Substitute.For<IStaticNodesManager>(),
            stateReader,
            new Enode(_enodeString),
            "/test/data",
            new ChainParameters(),
            Substitute.For<ITrustedNodesManager>(),
            subscriptionManager,
            new JsonRpcConfig());
    }

    private static IPeerPool CreatePeerPool(Peer peer)
    {
        ConcurrentDictionary<PublicKeyAsKey, Peer> peers = new();
        peers.TryAdd(TestItem.PublicKeyA, peer);
        IPeerPool peerPool = Substitute.For<IPeerPool>();
        peerPool.ActivePeers.Returns(peers);
        return peerPool;
    }

    private static Peer CreateTestPeer(string clientId, Capability[] capabilities, bool isStatic = false, bool isInbound = false)
    {
        Node node = new(TestItem.PublicKeyA, "127.0.0.1", 30303, isStatic) { ClientId = clientId };
        Peer peer = new(node);
        ISession session = Substitute.For<ISession>();
        session.RemoteHost.Returns("192.168.1.100");
        session.RemotePort.Returns(isInbound ? 45678 : 30303);
        session.LocalPort.Returns(30303);
        session.IsNetworkIdMatched.Returns(true);

        if (capabilities.Length > 0)
        {
            IP2PProtocolHandler protocolHandler = Substitute.For<IP2PProtocolHandler>();
            protocolHandler.GetCapabilities().Returns(capabilities);
            session.TryGetProtocolHandler("p2p", out Arg.Any<IProtocolHandler>())
                .Returns(x => { x[1] = protocolHandler; return true; });
        }
        else
        {
            session.TryGetProtocolHandler("p2p", out Arg.Any<IProtocolHandler>()).Returns(false);
        }

        if (isInbound)
        {
            peer.InSession = session;
        }
        else
        {
            peer.OutSession = session;
        }
        return peer;
    }
}
