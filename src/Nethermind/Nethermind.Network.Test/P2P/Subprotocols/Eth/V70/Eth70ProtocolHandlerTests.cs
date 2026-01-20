// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
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
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V70;
using Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V70;

public class Eth70ProtocolHandlerTests
{
    private ISession _session = null!;
    private IMessageSerializationService _svc = null!;
    private ISyncServer _syncManager = null!;
    private ITxPool _transactionPool = null!;
    private IGossipPolicy _gossipPolicy = null!;
    private ISpecProvider _specProvider = null!;
    private Block _genesisBlock = null!;
    private Eth70ProtocolHandler _handler = null!;
    private ITxGossipPolicy _txGossipPolicy = null!;
    private ITimerFactory _timerFactory = null!;

    [SetUp]
    public void Setup()
    {
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
        _syncManager.LowestBlock.Returns(0);
        _timerFactory = Substitute.For<ITimerFactory>();
        _txGossipPolicy = Substitute.For<ITxGossipPolicy>();
        _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(true);
        _txGossipPolicy.ShouldGossipTransaction(Arg.Any<Transaction>()).Returns(true);
        _svc = Build.A.SerializationService().WithEth70(_specProvider).TestObject;
        _handler = new Eth70ProtocolHandler(
            _session,
            _svc,
            new NodeStatsManager(_timerFactory, LimboLogs.Instance),
            _syncManager,
            RunImmediatelyScheduler.Instance,
            _transactionPool,
            _gossipPolicy,
            new ForkInfo(_specProvider, _syncManager),
            LimboLogs.Instance,
            Substitute.For<ITxPoolConfig>(),
            _specProvider,
            _txGossipPolicy);
        _handler.Init();
    }

    [TearDown]
    public void TearDown()
    {
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
        _handler.Name.Should().Be("eth70");
        _handler.ProtocolVersion.Should().Be(70);
        _handler.MessageIdSpaceSize.Should().Be(18);
        _handler.IncludeInTxPool.Should().BeTrue();
        _handler.ClientId.Should().Be(_session.Node?.ClientId);
        _handler.HeadHash.Should().BeNull();
        _handler.HeadNumber.Should().Be(0);
    }

    [Test]
    public void Should_mark_last_block_incomplete_when_truncated()
    {
        const int receiptCount = 20000;
        using var request = new GetReceiptsMessage70(1111, 0, new(new[] { Keccak.Zero }.ToPooledList()));
        TxReceipt[] hugeBlock = Enumerable.Repeat(Build.A.Receipt.WithAllFieldsFilled.TestObject, receiptCount).ToArray();
        _syncManager.GetReceipts(Arg.Any<Hash256>()).Returns(hugeBlock);

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        _session.Received().DeliverMessage(Arg.Is<ReceiptsMessage70>(m =>
            m.LastBlockIncomplete && m.EthMessage.TxReceipts.Count == 1 && m.EthMessage.TxReceipts[0].Length < receiptCount));
    }

    [Test]
    public async Task Should_request_additional_pages_until_complete()
    {
        TxReceipt[] receipts =
        {
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] },
            new() { GasUsedTotal = GasCostOf.Transaction * 2, Logs = [] },
            new() { GasUsedTotal = GasCostOf.Transaction * 3, Logs = [] }
        };

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            TxReceipt[] payload = sent.FirstBlockReceiptIndex == 0
                ? receipts.Take(2).ToArray()
                : receipts.Skip((int)sent.FirstBlockReceiptIndex).ToArray();

            ReceiptsMessage70 response = new(sent.RequestId, new(new[] { payload }.ToPooledList()), sent.FirstBlockReceiptIndex == 0);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        Task<IOwnedReadOnlyList<TxReceipt[]>> task = _handler.GetReceipts(new[] { Keccak.Zero }, CancellationToken.None);

        IOwnedReadOnlyList<TxReceipt[]> result = await task;
        result.Should().HaveCount(1);
        result[0].Should().BeEquivalentTo(receipts);
    }

    [Test]
    public async Task Should_merge_partial_first_block_and_continue_to_next_block()
    {
        TxReceipt[] block1 =
        {
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] },
            new() { GasUsedTotal = GasCostOf.Transaction * 2, Logs = [] },
            new() { GasUsedTotal = GasCostOf.Transaction * 3, Logs = [] }
        };

        TxReceipt[] block2 =
        {
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] },
            new() { GasUsedTotal = GasCostOf.Transaction * 2, Logs = [] }
        };

        using ArrayPoolList<long> seenOffsets = new(2);

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            seenOffsets.Add(sent.FirstBlockReceiptIndex);

            TxReceipt[][] payload = sent.FirstBlockReceiptIndex == 0
                ? new[] { block1.Take(2).ToArray() }
                : new[] { block1.Skip((int)sent.FirstBlockReceiptIndex).ToArray(), block2 };

            bool lastBlockIncomplete = sent.FirstBlockReceiptIndex == 0;
            ReceiptsMessage70 response = new(sent.RequestId, new(payload.ToPooledList()), lastBlockIncomplete);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        Task<IOwnedReadOnlyList<TxReceipt[]>> task = _handler.GetReceipts(new[] { Keccak.Zero, TestItem.KeccakA }, CancellationToken.None);

        IOwnedReadOnlyList<TxReceipt[]> result = await task;
        result.Should().HaveCount(2);
        result[0].Should().BeEquivalentTo(block1);
        result[1].Should().BeEquivalentTo(block2);
        seenOffsets.AsSpan().ToArray().Should().Equal(0, 2);
    }

    [Test]
    public async Task Should_reject_when_receipts_response_below_minimum_size()
    {
        TxReceipt[] receipts =
        {
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] }
        };

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            ReceiptsMessage70 response = new(sent.RequestId, new(new[] { receipts }.ToPooledList()), false);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        Func<Task> act = async () => await _handler.GetReceipts(new[] { Keccak.Zero, TestItem.KeccakA }, CancellationToken.None);

        await act.Should().ThrowAsync<SubprotocolException>()
            .WithMessage("*non-truncated receipts response below minimum size*");
    }

    [Test]
    public void Should_reject_when_intrinsic_exceeds_block_gas()
    {
        TxReceipt[] receipts =
        {
            new() { GasUsedTotal = GasCostOf.Transaction / 2, Logs = [] },
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] }
        };

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            ReceiptsMessage70 response = new(sent.RequestId, new(new[] { receipts }.ToPooledList()), false);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        Func<Task> act = async () => await _handler.GetReceipts(new[] { Keccak.Zero }, CancellationToken.None);

        act.Should().ThrowAsync<SubprotocolException>();
    }

    [Test]
    public void Should_reject_when_logs_gas_exceeds_block_gas()
    {
        TxReceipt[] receipts =
        {
            new()
            {
                GasUsedTotal = 500,
                Logs = new[] { new LogEntry(TestItem.AddressA, new byte[100], Array.Empty<Hash256>()) }
            }
        };

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            ReceiptsMessage70 response = new(sent.RequestId, new(new[] { receipts }.ToPooledList()), false);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        Func<Task> act = async () => await _handler.GetReceipts(new[] { Keccak.Zero }, CancellationToken.None);

        act.Should().ThrowAsync<SubprotocolException>();
    }

    private void HandleIncomingStatusMessage()
    {
        using var statusMsg = new StatusMessage69 { ProtocolVersion = 70, GenesisHash = _genesisBlock.Hash!, LatestBlockHash = _genesisBlock.Hash! };

        IByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg);
        statusPacket.ReadByte();
        _handler.HandleMessage(new ZeroPacket(statusPacket) { PacketType = 0 });
    }

    private void HandleZeroMessage<T>(T msg, int messageCode) where T : MessageBase
    {
        IByteBuffer packet = _svc!.ZeroSerialize(msg);
        packet.ReadByte();
        _handler!.HandleMessage(new ZeroPacket(packet) { PacketType = (byte)messageCode });
    }
}
