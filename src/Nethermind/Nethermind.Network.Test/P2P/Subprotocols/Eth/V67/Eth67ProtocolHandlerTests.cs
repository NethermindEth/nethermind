// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
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
    private ISession _session = null!;
    private IMessageSerializationService _svc = null!;
    private ISyncServer _syncManager = null!;
    private ITxPool _transactionPool = null!;
    private IGossipPolicy _gossipPolicy = null!;
    private ISpecProvider _specProvider = null!;
    private Block _genesisBlock = null!;
    private Eth66ProtocolHandler _handler = null!;

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
            RunImmediatelyScheduler.Instance,
            _transactionPool,
            _gossipPolicy,
            new ForkInfo(_specProvider, _syncManager),
            LimboLogs.Instance);
        _handler.Init();
    }

    [TearDown]
    public void TearDown()
    {
        _handler?.Dispose();
        _session?.Dispose();
        _syncManager?.Dispose();
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
        using GetNodeDataMessage msg63 = new(new[] { Keccak.Zero, TestItem.KeccakA }.ToPooledList());
        using Network.P2P.Subprotocols.Eth.V66.Messages.GetNodeDataMessage msg66 = new(1111, msg63);

        HandleIncomingStatusMessage();
        HandleZeroMessage(msg66, Eth66MessageCode.GetNodeData);
        _session.DidNotReceive().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.NodeDataMessage>());
    }

    [Test]
    public void Can_ignore_node_data_and_not_throw_when_receiving_unrequested_node_data()
    {
        using NodeDataMessage msg63 = new(new ByteArrayListAdapter(ArrayPoolList<byte[]>.Empty()));
        using Network.P2P.Subprotocols.Eth.V66.Messages.NodeDataMessage msg66 = new(1111, msg63);

        HandleIncomingStatusMessage();
        System.Action act = () => HandleZeroMessage(msg66, Eth66MessageCode.NodeData);
        act.Should().NotThrow<SubprotocolException>();
    }

    [Test]
    public void Can_handle_eth66_messages_other_than_GetNodeData_and_NodeData() // e.g. GetBlockHeadersMessage
    {
        using GetBlockHeadersMessage msg62 = new();
        using Network.P2P.Subprotocols.Eth.V66.Messages.GetBlockHeadersMessage msg66 = new(1111, msg62);

        HandleIncomingStatusMessage();
        HandleZeroMessage(msg66, Eth66MessageCode.GetBlockHeaders);
        _session.Received().DeliverMessage(Arg.Any<Network.P2P.Subprotocols.Eth.V66.Messages.BlockHeadersMessage>());
    }

    private void HandleZeroMessage<T>(T msg, int messageCode) where T : MessageBase
    {
        using DisposableByteBuffer getZeroPacket = _svc.ZeroSerialize(msg).AsDisposable();
        getZeroPacket.ReadByte();
        _handler.HandleMessage(new ZeroPacket(getZeroPacket) { PacketType = (byte)messageCode });
    }

    private void HandleIncomingStatusMessage()
    {
        StatusMessage statusMsg = new();
        statusMsg.GenesisHash = _genesisBlock.Hash;
        statusMsg.BestHash = _genesisBlock.Hash;

        using DisposableByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg).AsDisposable();
        statusPacket.ReadByte();
        _handler.HandleMessage(new ZeroPacket(statusPacket) { PacketType = 0 });
    }
}
