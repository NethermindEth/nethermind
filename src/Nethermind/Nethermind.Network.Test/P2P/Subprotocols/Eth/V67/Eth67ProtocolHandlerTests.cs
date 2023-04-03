// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V67;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V67;

[TestFixture, Parallelizable(ParallelScope.Self)]
public class Eth67ProtocolHandlerTests
{
    private ISession _session;
    private IMessageSerializationService _svc;
    private ISyncServer _syncManager;
    private ITxPool _transactionPool;
    private IPooledTxsRequestor _pooledTxsRequestor;
    private IGossipPolicy _gossipPolicy;
    private ISpecProvider _specProvider;
    private Block _genesisBlock;
    private Eth66ProtocolHandler _handler;

    [SetUp]
    public void Setup()
    {
        _svc = Build.A.SerializationService().WithEth66().TestObject;

        NetworkDiagTracer.IsEnabled = true;

        _session = Substitute.For<ISession>();
        Node node = new(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303));
        _session.Node.Returns(node);
        _syncManager = Substitute.For<ISyncServer>();
        _transactionPool = Substitute.For<ITxPool>();
        _pooledTxsRequestor = Substitute.For<IPooledTxsRequestor>();
        _specProvider = Substitute.For<ISpecProvider>();
        _gossipPolicy = Substitute.For<IGossipPolicy>();
        _genesisBlock = Build.A.Block.Genesis.TestObject;
        _syncManager.Head.Returns(_genesisBlock.Header);
        _syncManager.Genesis.Returns(_genesisBlock.Header);
        ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
        _handler = new Eth67ProtocolHandler(
            _session,
            _svc,
            new NodeStatsManager(timerFactory, LimboLogs.Instance),
            _syncManager,
            _transactionPool,
            _pooledTxsRequestor,
            _gossipPolicy,
            new ForkInfo(_specProvider, _genesisBlock.Header.Hash!),
            LimboLogs.Instance);
        _handler.Init();
    }

    [TearDown]
    public void TearDown()
    {
        _handler.Dispose();
    }

    [Test]
    public void Metadata_correct()
    {
        _handler.ProtocolCode.Should().Be("eth");
        _handler.Name.Should().Be("eth67");
        _handler.ProtocolVersion.Should().Be(67);
        _handler.MessageIdSpaceSize.Should().Be(17);
        _handler.IncludeInTxPool.Should().BeTrue();
        _handler.ClientId.Should().Be(_session.Node?.ClientId);
        _handler.HeadHash.Should().BeNull();
        _handler.HeadNumber.Should().Be(0);
    }

    [Test]
    public void Can_ignore_get_node_data()
    {
        var msg63 = new GetNodeDataMessage(new[] { Keccak.Zero, TestItem.KeccakA });
        var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.GetNodeDataMessage(1111, msg63);

        HandleIncomingStatusMessage();
        HandleZeroMessage(msg66, Eth66MessageCode.GetNodeData);
        _session.DidNotReceive().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.NodeDataMessage>());
    }

    [Test]
    public void Can_ignore_node_data_and_not_throw_when_receiving_unrequested_node_data()
    {
        var msg63 = new NodeDataMessage(System.Array.Empty<byte[]>());
        var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.NodeDataMessage(1111, msg63);

        HandleIncomingStatusMessage();
        System.Action act = () => HandleZeroMessage(msg66, Eth66MessageCode.NodeData);
        act.Should().NotThrow<SubprotocolException>();
    }

    [Test]
    public void Can_handle_eth66_messages_other_than_GetNodeData_and_NodeData() // e.g. GetBlockHeadersMessage
    {
        var msg62 = new GetBlockHeadersMessage();
        var msg66 = new Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockHeadersMessage(1111, msg62);

        HandleIncomingStatusMessage();
        HandleZeroMessage(msg66, Eth66MessageCode.GetBlockHeaders);
        _session.Received().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.BlockHeadersMessage>());
    }

    private void HandleZeroMessage<T>(T msg, int messageCode) where T : MessageBase
    {
        IByteBuffer getZeroPacket = _svc.ZeroSerialize(msg);
        getZeroPacket.ReadByte();
        _handler.HandleMessage(new ZeroPacket(getZeroPacket) { PacketType = (byte)messageCode });
    }

    private void HandleIncomingStatusMessage()
    {
        var statusMsg = new StatusMessage();
        statusMsg.GenesisHash = _genesisBlock.Hash;
        statusMsg.BestHash = _genesisBlock.Hash;

        IByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg);
        statusPacket.ReadByte();
        _handler.HandleMessage(new ZeroPacket(statusPacket) { PacketType = 0 });
    }
}
