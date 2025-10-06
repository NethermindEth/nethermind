// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Blockchain.Find;
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
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using GetReceiptsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetReceiptsMessage;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V69;

public class Eth69ProtocolHandlerTests
{
    private ISession _session = null!;
    private IMessageSerializationService _svc = null!;
    private ISyncServer _syncManager = null!;
    private ITxPool _transactionPool = null!;
    private IPooledTxsRequestor _pooledTxsRequestor = null!;
    private IGossipPolicy _gossipPolicy = null!;
    private ISpecProvider _specProvider = null!;
    private Block _genesisBlock = null!;
    private Eth69ProtocolHandler _handler = null!;
    private ITxGossipPolicy _txGossipPolicy = null!;
    private ITimerFactory _timerFactory = null!;
    private IBlockFinder _blockFinder = null!;

    [SetUp]
    public void Setup()
    {
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
        _timerFactory = Substitute.For<ITimerFactory>();
        _txGossipPolicy = Substitute.For<ITxGossipPolicy>();
        _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(true);
        _txGossipPolicy.ShouldGossipTransaction(Arg.Any<Transaction>()).Returns(true);
        _svc = Build.A.SerializationService().WithEth69(_specProvider).TestObject;
        _blockFinder = Substitute.For<IBlockFinder>();
        _blockFinder.GetLowestBlock().Returns(3);
        _handler = new Eth69ProtocolHandler(
            _session,
            _svc,
            new NodeStatsManager(_timerFactory, LimboLogs.Instance),
            _syncManager,
            RunImmediatelyScheduler.Instance,
            _transactionPool,
            _pooledTxsRequestor,
            _gossipPolicy,
            new ForkInfo(_specProvider, _syncManager),
            _blockFinder,
            LimboLogs.Instance,
            _txGossipPolicy);
        _handler.Init();
    }

    [TearDown]
    public void TearDown()
    {
        // Dispose received messages
        _session?.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(_session.DeliverMessage))
            .ForEach(c => (c.GetArguments()[0] as P2PMessage)?.Dispose());

        _handler.Dispose();
        _session.Dispose();
        _syncManager.Dispose();
    }

    [Test]
    public void Metadata_correct()
    {
        _handler.ProtocolCode.Should().Be("eth");
        _handler.Name.Should().Be("eth69");
        _handler.ProtocolVersion.Should().Be(69);
        _handler.MessageIdSpaceSize.Should().Be(18);
        _handler.IncludeInTxPool.Should().BeTrue();
        _handler.ClientId.Should().Be(_session.Node?.ClientId);
        _handler.HeadHash.Should().BeNull();
        _handler.HeadNumber.Should().Be(0);
    }

    [Test]
    public void Can_handle_status_message()
    {
        HandleIncomingStatusMessage();

        _handler.TotalDifficulty.Should().Be(null);
    }

    [Test] // From Eth62ProtocolHandlerTests
    public void Should_fail_on_receiving_status_message_for_the_second_time()
    {
        HandleIncomingStatusMessage();
        Assert.Throws<SubprotocolException>(HandleIncomingStatusMessage);
    }

    [Test] // From Eth63ProtocolHandlerTests
    public void Should_not_send_receipts_message_larger_than_2MB()
    {
        const int count = 512;
        using var msg = new GetReceiptsMessage66(1111, new(Enumerable.Repeat(Keccak.Zero, count).ToPooledList(count)));
        _syncManager.GetReceipts(Arg.Any<Hash256>()).Returns(Enumerable.Repeat(Build.A.Receipt.WithAllFieldsFilled.TestObject, count).ToArray());

        HandleIncomingStatusMessage();
        HandleZeroMessage(msg, Eth63MessageCode.GetReceipts);

        _session.Received().DeliverMessage(Arg.Is<ReceiptsMessage69>(r => r.EthMessage.TxReceipts.Count == 14));
    }

    [Test]
    public async Task Can_request_and_handle_receipts()
    {
        const int count = 100;

        TxReceipt[][] receipts = Enumerable.Repeat(
            Enumerable.Repeat(Build.A.Receipt.WithAllFieldsFilled.TestObject, 10).ToArray(), count
        ).ToArray();
        using ReceiptsMessage69 msg = new(0, new(receipts.ToPooledList()));

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage66>())).Do(call =>
        {
            var message = (GetReceiptsMessage66)call[0];
            msg.RequestId = message.RequestId;
        });

        HandleIncomingStatusMessage();
        Task<IOwnedReadOnlyList<TxReceipt[]>> getReceiptsTask = _handler.GetReceipts(
            Enumerable.Repeat(Keccak.Zero, count).ToArray(), CancellationToken.None
        );

        _session.Received(1).DeliverMessage(Arg.Any<GetReceiptsMessage66>());
        HandleZeroMessage(msg, Eth63MessageCode.Receipts);

        IOwnedReadOnlyList<TxReceipt[]>? response = await getReceiptsTask;

        response.Should().HaveCount(count);
    }

    [Test]
    public void Should_handle_GetReceipts()
    {
        using var msg66 = new GetReceiptsMessage(1111, new(new[] { Keccak.Zero, TestItem.KeccakA }.ToPooledList()));

        HandleIncomingStatusMessage();
        HandleZeroMessage(msg66, Eth63MessageCode.GetReceipts);
        _session.Received(1).DeliverMessage(Arg.Any<ReceiptsMessage69>());
    }

    [Test]
    public void Should_throw_when_receiving_unrequested_receipts()
    {
        using var msg66 = new ReceiptsMessage(1111, new(ArrayPoolList<TxReceipt[]>.Empty()));

        HandleIncomingStatusMessage();
        Action action = () => HandleZeroMessage(msg66, Eth66MessageCode.Receipts);
        action.Should().Throw<SubprotocolException>();
    }

    [Test]
    public void Should_send_BlockRangeUpdate()
    {
        var (earliest, latest) = (Build.A.BlockHeader.WithNumber(0).TestObject, Build.A.BlockHeader.WithNumber(42).TestObject);
        _handler.NotifyOfNewRange(earliest, latest);

        _session.Received(1).DeliverMessage(Arg.Is<BlockRangeUpdateMessage>(m =>
            m.EarliestBlock == earliest.Number && m.LatestBlock == latest.Number && m.LatestBlockHash == latest.Hash)
        );
    }

    [Test]
    public void Should_not_send_invalid_BlockRangeUpdate()
    {
        var (earliest, latest) = (Build.A.BlockHeader.WithNumber(42).TestObject, Build.A.BlockHeader.WithNumber(0).TestObject);

        Assert.Catch<Exception>(() => _handler.NotifyOfNewRange(earliest, latest));

        _session.DidNotReceive().DeliverMessage(Arg.Any<BlockRangeUpdateMessage>());
    }

    [Test]
    public void Should_handle_BlockRangeUpdate()
    {
        using var msg = new BlockRangeUpdateMessage
        {
            EarliestBlock = 1,
            LatestBlock = 2,
            LatestBlockHash = Keccak.Compute("2")
        };

        HandleIncomingStatusMessage();
        HandleZeroMessage(msg, Eth69MessageCode.BlockRangeUpdate);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_handler.HeadNumber, Is.EqualTo(msg.LatestBlock));
            Assert.That(_handler.HeadHash, Is.EqualTo(msg.LatestBlockHash));
        }
    }

    [Test]
    public void Should_disconnect_on_invalid_BlockRangeUpdate()
    {
        using var msg = new BlockRangeUpdateMessage
        {
            EarliestBlock = 2,
            LatestBlock = 1,
            LatestBlockHash = Keccak.Compute("2")
        };

        HandleIncomingStatusMessage();
        HandleZeroMessage(msg, Eth69MessageCode.BlockRangeUpdate);

        _session.Received().InitiateDisconnect(DisconnectReason.InvalidBlockRangeUpdate, Arg.Any<string>());
    }

    [Test]
    public void Should_disconnect_on_invalid_BlockRangeUpdate_empty_hash()
    {
        using var msg = new BlockRangeUpdateMessage
        {
            EarliestBlock = 1,
            LatestBlock = 2,
            LatestBlockHash = Keccak.Zero
        };

        HandleIncomingStatusMessage();
        HandleZeroMessage(msg, Eth69MessageCode.BlockRangeUpdate);

        _session.Received().InitiateDisconnect(DisconnectReason.InvalidBlockRangeUpdate, Arg.Any<string>());
    }

    [Test]
    public void On_init_sends_a_status_message()
    {
        // init is called in Setup
        _session.Received(1).DeliverMessage(Arg.Is<StatusMessage69>(m =>
            m.ProtocolVersion == 69
            && m.Protocol == Protocol.Eth
            && m.GenesisHash == _genesisBlock.Hash
            && m.LatestBlockHash == _genesisBlock.Hash
            && m.EarliestBlock == 3));
    }

    private void HandleIncomingStatusMessage()
    {
        using var statusMsg = new StatusMessage69 { ProtocolVersion = 69, GenesisHash = _genesisBlock.Hash!, LatestBlockHash = _genesisBlock.Hash! };

        IByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg);
        statusPacket.ReadByte();
        _handler.HandleMessage(new ZeroPacket(statusPacket) { PacketType = 0 });
    }

    private void HandleZeroMessage<T>(T msg, int messageCode) where T : MessageBase
    {
        IByteBuffer uOpsPacket = _svc!.ZeroSerialize(msg);
        uOpsPacket.ReadByte();
        _handler!.HandleMessage(new ZeroPacket(uOpsPacket) { PacketType = (byte)messageCode });
    }
}
