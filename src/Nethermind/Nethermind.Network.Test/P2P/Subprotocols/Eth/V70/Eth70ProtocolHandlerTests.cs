// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
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
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V70;
using Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Serialization.Rlp;
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
        SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit = 2UL.MiB;
        SyncPeerProtocolHandlerBase.HardOutgoingReceiptsMessageSizeLimit = 10UL.MiB;
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
        _syncManager.FindHeader(Arg.Any<Hash256>()).Returns(_genesisBlock.Header);
        _syncManager.LowestBlock.Returns(0UL);
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_handler.ProtocolCode, Is.EqualTo("eth"));
            Assert.That(_handler.Name, Is.EqualTo("eth70"));
            Assert.That(_handler.ProtocolVersion, Is.EqualTo(70));
            Assert.That(_handler.MessageIdSpaceSize, Is.EqualTo(18));
            Assert.That(_handler.IncludeInTxPool, Is.True);
            Assert.That(_handler.ClientId, Is.EqualTo(_session.Node?.ClientId));
            Assert.That(_handler.HeadHash, Is.Null);
            Assert.That(_handler.HeadNumber, Is.EqualTo(0));
        }
    }

    [Test]
    public void Default_size_limits_match_eth_protocol_limits()
    {
        Assert.That(SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit, Is.EqualTo(2UL.MiB));
        Assert.That(SyncPeerProtocolHandlerBase.HardOutgoingReceiptsMessageSizeLimit, Is.EqualTo(10UL.MiB));
    }

    [Test]
    public void Should_mark_last_block_incomplete_when_truncated()
    {
        SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit = 1UL.MB;
        SyncPeerProtocolHandlerBase.HardOutgoingReceiptsMessageSizeLimit = 1UL.MB;

        const int receiptCount = 20000;
        using GetReceiptsMessage70 request = new(1111, 0, new[] { Keccak.Zero }.ToPooledList());
        TxReceipt[] hugeBlock = Enumerable.Repeat(Build.A.Receipt.WithAllFieldsFilled.TestObject, receiptCount).ToArray();
        _syncManager.GetReceipts(Arg.Any<Hash256>()).Returns(hugeBlock);

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        _session.Received().DeliverMessage(Arg.Is<ReceiptsMessage70>(m =>
            m.LastBlockIncomplete && m.TxReceipts.Count == 1 && m.TxReceipts[0].Length < receiptCount));
    }

    [Test]
    public void Should_return_empty_receipts_block_when_local_block_has_no_transactions()
    {
        using GetReceiptsMessage70 request = new(1111, 0, new[] { Keccak.Zero }.ToPooledList());
        _syncManager.GetReceipts(Arg.Any<Hash256>()).Returns(Array.Empty<TxReceipt>());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        _session.Received().DeliverMessage(Arg.Is<ReceiptsMessage70>(m =>
            m.TxReceipts.Count == 1 && m.TxReceipts[0].Length == 0 && !m.LastBlockIncomplete));
    }

    [Test]
    public void Should_stop_response_when_receipts_are_not_known_locally()
    {
        using GetReceiptsMessage70 request = new(1111, 0, new[] { Keccak.Zero }.ToPooledList());
        _syncManager.GetReceipts(Arg.Any<Hash256>()).Returns((TxReceipt[]?)null);

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        _session.Received().DeliverMessage(Arg.Is<ReceiptsMessage70>(m =>
            m.TxReceipts.Count == 0 && !m.LastBlockIncomplete));
    }

    [Test]
    public void Should_throw_when_receiving_GetReceipts_before_status()
    {
        using GetReceiptsMessage70 request = new(1111, 0, new[] { Keccak.Zero }.ToPooledList());

        Action action = () => HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        Assert.That(action, Throws.TypeOf<SubprotocolException>());
        _session.DidNotReceive().DeliverMessage(Arg.Any<ReceiptsMessage70>());
    }

    [TestCaseSource(nameof(SingleBlockReceiptResponseCases))]
    public void Should_return_expected_receipts_for_first_block_receipt_index(
        long firstBlockReceiptIndex,
        int expectedLength,
        int? expectedFirstReceiptIndex,
        int? expectedLastReceiptIndex)
    {
        TxReceipt[] receipts = BuildSequentialReceipts(3);

        using GetReceiptsMessage70 request = new(1111, firstBlockReceiptIndex, new[] { Keccak.Zero }.ToPooledList());
        _syncManager.GetReceipts(Arg.Any<Hash256>()).Returns(receipts);

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        _session.Received().DeliverMessage(Arg.Is<ReceiptsMessage70>(m =>
            m.TxReceipts.Count == 1 &&
            m.TxReceipts[0].Length == expectedLength &&
            (expectedLength == 0 || m.TxReceipts[0][0].GasUsedTotal == receipts[expectedFirstReceiptIndex!.Value].GasUsedTotal) &&
            (expectedLength == 0 || m.TxReceipts[0][expectedLength - 1].GasUsedTotal == receipts[expectedLastReceiptIndex!.Value].GasUsedTotal) &&
            !m.LastBlockIncomplete));
    }

    [TestCaseSource(nameof(InvalidFirstBlockReceiptIndexCases))]
    public void Should_disconnect_when_first_block_receipt_index_is_invalid(long firstBlockReceiptIndex)
    {
        TxReceipt[] receipts = BuildSequentialReceipts(2);

        using GetReceiptsMessage70 request = new(1111, firstBlockReceiptIndex, new[] { Keccak.Zero }.ToPooledList());
        _syncManager.GetReceipts(Arg.Any<Hash256>()).Returns(receipts);

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        _session.Received().InitiateDisconnect(DisconnectReason.BackgroundTaskFailure,
            Arg.Is<string>(s => s.Contains("Invalid firstBlockReceiptIndex")));
    }

    [Test]
    public void Should_disconnect_with_subprotocol_error_when_first_block_receipt_index_exceeds_int_max_value()
    {
        TxReceipt[] receipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] },
            new() { GasUsedTotal = GasCostOf.Transaction * 2, Logs = [] }
        ];

        using GetReceiptsMessage70 request = new(1111, (long)int.MaxValue + 1, new[] { Keccak.Zero }.ToPooledList());
        _syncManager.GetReceipts(Arg.Any<Hash256>()).Returns(receipts);

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        _session.Received().InitiateDisconnect(DisconnectReason.BackgroundTaskFailure,
            Arg.Is<string>(s => s.Contains("Invalid firstBlockReceiptIndex")));
    }

    [Test]
    public async Task Should_request_additional_pages_until_complete()
    {
        SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit = 75;

        TxReceipt[] receipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] },
            new() { GasUsedTotal = GasCostOf.Transaction * 2, Logs = [] },
            new() { GasUsedTotal = GasCostOf.Transaction * 3, Logs = [] }
        ];

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            TxReceipt[] payload = sent.FirstBlockReceiptIndex == 0
                ? receipts.Take(2).ToArray()
                : receipts.Skip((int)sent.FirstBlockReceiptIndex).ToArray();

            ReceiptsMessage70 response = new(sent.RequestId, new[] { payload }.ToPooledList(), sent.FirstBlockReceiptIndex == 0);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        Task<IOwnedReadOnlyList<TxReceipt[]>> task = _handler.GetReceipts(new[] { Keccak.Zero }, CancellationToken.None);

        using IOwnedReadOnlyList<TxReceipt[]> result = await task;
        Assert.That(result, Has.Count.EqualTo(1));
        AssertReceiptsEqual(result[0], receipts);
    }

    [Test]
    public async Task Should_not_copy_remaining_hashes_when_building_paged_receipts_requests()
    {
        SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit = 75;

        TxReceipt[] receipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] },
            new() { GasUsedTotal = GasCostOf.Transaction * 2, Logs = [] },
            new() { GasUsedTotal = GasCostOf.Transaction * 3, Logs = [] }
        ];

        CountingReadOnlyList<Hash256> blockHashes = new([Keccak.Zero, TestItem.KeccakA, TestItem.KeccakB]);
        int requestCount = 0;

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            requestCount++;
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            TxReceipt[] payload = sent.FirstBlockReceiptIndex == 0
                ? [receipts[0], receipts[1]]
                : [receipts[2]];

            using ReceiptsMessage70 response = new(sent.RequestId, new[] { payload }.ToPooledList(), sent.FirstBlockReceiptIndex == 0);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        using IOwnedReadOnlyList<TxReceipt[]> result = await _handler.GetReceipts(blockHashes, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Count, Is.EqualTo(1));
            AssertReceiptsEqual(result[0], receipts);
            Assert.That(requestCount, Is.EqualTo(2));
            Assert.That(blockHashes.IndexerReadCount, Is.EqualTo(blockHashes.Count));
        }
    }

    [Test]
    public async Task Should_merge_partial_first_block_and_continue_to_next_block()
    {
        SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit = 75;

        TxReceipt[] block1 =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] },
            new() { GasUsedTotal = GasCostOf.Transaction * 2, Logs = [] },
            new() { GasUsedTotal = GasCostOf.Transaction * 3, Logs = [] }
        ];

        TxReceipt[] block2 =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] },
            new() { GasUsedTotal = GasCostOf.Transaction * 2, Logs = [] }
        ];

        using ArrayPoolList<long> seenOffsets = new(2);

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            seenOffsets.Add(sent.FirstBlockReceiptIndex);

            TxReceipt[][] payload = sent.FirstBlockReceiptIndex == 0
                ? [block1.Take(2).ToArray()]
                : [block1.Skip((int)sent.FirstBlockReceiptIndex).ToArray(), block2];

            bool lastBlockIncomplete = sent.FirstBlockReceiptIndex == 0;
            ReceiptsMessage70 response = new(sent.RequestId, payload.ToPooledList(), lastBlockIncomplete);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        Task<IOwnedReadOnlyList<TxReceipt[]>> task = _handler.GetReceipts(new[] { Keccak.Zero, TestItem.KeccakA }, CancellationToken.None);

        using IOwnedReadOnlyList<TxReceipt[]> result = await task;
        Assert.That(result, Has.Count.EqualTo(2));
        AssertReceiptsEqual(result[0], block1);
        AssertReceiptsEqual(result[1], block2);
        Assert.That(seenOffsets.AsSpan().ToArray(), Is.EqualTo(new[] { 0L, 2L }));
    }

    [Test]
    public async Task Should_accept_empty_receipts_block_when_requesting_from_peer()
    {
        TxReceipt[] block2Receipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] }
        ];

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            TxReceipt[][] payload = [[], block2Receipts];

            using ReceiptsMessage70 response = new(sent.RequestId, payload.ToPooledList(), lastBlockIncomplete: false);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        using IOwnedReadOnlyList<TxReceipt[]> result = await _handler.GetReceipts(new[] { Keccak.Zero, TestItem.KeccakA }, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Has.Count.EqualTo(2));
            Assert.That(result[0], Is.Empty);
            Assert.That(result[1], Has.Length.EqualTo(block2Receipts.Length));
            Assert.That(result[1][0].GasUsedTotal, Is.EqualTo(block2Receipts[0].GasUsedTotal));
            Assert.That(result[1][0].Logs, Is.Empty);
        }
    }

    [TestCaseSource(nameof(EmptyReceiptsPayloadCases))]
    public async Task Should_handle_empty_receipts_payload_when_requesting_from_peer(
        EmptyReceiptsPayloadScenario scenario,
        string? expectedExceptionMessage,
        int expectedRequestCount,
        int expectedResultCount)
    {
        SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit = 75;

        TxReceipt[] firstPage =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] }
        ];

        int requestCount = 0;

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            requestCount++;
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            using ReceiptsMessage70 response = BuildEmptyReceiptsPayloadResponse(sent, scenario, firstPage);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        Func<Task<IOwnedReadOnlyList<TxReceipt[]>>> act = async () =>
            await _handler.GetReceipts(new[] { Keccak.Zero, TestItem.KeccakA }, CancellationToken.None);

        if (expectedExceptionMessage is null)
        {
            using IOwnedReadOnlyList<TxReceipt[]> result = await act();
            Assert.That(result, Has.Count.EqualTo(expectedResultCount));
        }
        else
        {
            SubprotocolException? exception = Assert.ThrowsAsync<SubprotocolException>(async () =>
            {
                using IOwnedReadOnlyList<TxReceipt[]> result = await act();
            });
            Assert.That(exception?.Message, Is.EqualTo(expectedExceptionMessage));
        }

        Assert.That(requestCount, Is.EqualTo(expectedRequestCount));
    }

    [TestCaseSource(nameof(InvalidPartialContinuationCases))]
    public void Should_reject_invalid_partial_continuation(InvalidPartialContinuationCase testCase)
    {
        SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit = 75;

        _syncManager.FindHeader(Keccak.Zero).Returns(Build.A.BlockHeader.WithGasUsed(testCase.FirstBlockGasUsed).TestObject);
        if (testCase.SecondBlockGasUsed is not null)
        {
            _syncManager.FindHeader(TestItem.KeccakA).Returns(Build.A.BlockHeader.WithGasUsed(testCase.SecondBlockGasUsed.Value).TestObject);
        }

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            if (!testCase.Responses.TryGetValue(sent.FirstBlockReceiptIndex, out ReceiptsPageResponse? pageResponse))
            {
                throw new InvalidOperationException($"Unexpected first block receipt index {sent.FirstBlockReceiptIndex}");
            }

            using ReceiptsMessage70 response = new(sent.RequestId, pageResponse.Receipts.ToPooledList(), pageResponse.LastBlockIncomplete);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        Func<Task> act = async () =>
        {
            using IOwnedReadOnlyList<TxReceipt[]> result = await _handler.GetReceipts(testCase.RequestedHashes, CancellationToken.None);
        };

        SubprotocolException? exception = Assert.ThrowsAsync<SubprotocolException>(async () => await act());
        Assert.That(exception?.Message, Is.EqualTo(testCase.ExpectedExceptionMessage));
    }

    [Test]
    public void Should_reject_when_receipts_response_below_minimum_size()
    {
        TxReceipt[] receipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] }
        ];

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            ReceiptsMessage70 response = new(sent.RequestId, new[] { receipts }.ToPooledList(), true);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        Func<Task> act = async () => await _handler.GetReceipts(new[] { Keccak.Zero, TestItem.KeccakA }, CancellationToken.None);

        SubprotocolException? exception = Assert.ThrowsAsync<SubprotocolException>(async () => await act());
        Assert.That(exception?.Message, Does.StartWith("Received partial receipts response below minimum size"));
    }

    [Test]
    public void Should_reject_when_intrinsic_exceeds_block_gas()
    {
        TxReceipt[] receipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction / 2, Logs = [] },
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] }
        ];

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            ReceiptsMessage70 response = new(sent.RequestId, new[] { receipts }.ToPooledList(), true);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        Func<Task> act = async () => await _handler.GetReceipts(new[] { Keccak.Zero }, CancellationToken.None);

        Assert.ThrowsAsync<SubprotocolException>(async () => await act());
    }

    [Test]
    public async Task Should_accept_when_logs_gas_exceeds_post_refund_gas_used()
    {
        TxReceipt[] receipts =
        [
            new()
            {
                GasUsedTotal = GasCostOf.Transaction,
                Logs = [new LogEntry(TestItem.AddressA, new byte[100], Array.Empty<Hash256>())]
            }
        ];

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            ReceiptsMessage70 response = new(sent.RequestId, new[] { receipts }.ToPooledList(), false);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        using IOwnedReadOnlyList<TxReceipt[]> result = await _handler.GetReceipts(new[] { Keccak.Zero }, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(1));
        AssertReceiptsEqual(result[0], receipts);
    }

    [Test]
    public async Task Should_accept_receipts_when_transaction_count_and_size_bounds_match()
    {
        Hash256 blockHash = TestItem.KeccakA;
        TxReceipt[] receipts = BuildSequentialReceipts(2);
        SetupBlockMetadata(blockHash, 1_000_000, GasCostOf.Transaction * 2, 100_000, 100_000);

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            using ReceiptsMessage70 response = new(sent.RequestId, new[] { receipts }.ToPooledList(), false);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        using IOwnedReadOnlyList<TxReceipt[]> result = await _handler.GetReceipts(new[] { blockHash }, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(1));
        AssertReceiptsEqual(result[0], receipts);
    }

    [Test]
    public async Task Should_skip_transaction_metadata_validation_when_block_body_is_missing()
    {
        Hash256 blockHash = TestItem.KeccakA;
        TxReceipt[] receipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] }
        ];
        BlockHeader header = Build.A.BlockHeader
            .WithHash(blockHash)
            .WithNumber(1)
            .WithGasLimit(1_000_000)
            .WithGasUsed(GasCostOf.Transaction)
            .WithTransactionsRoot(TestItem.KeccakB)
            .TestObject;
        Block blockWithMissingBody = new(header);

        _syncManager.FindHeader(blockHash).Returns(header);
        _syncManager.Find(blockHash).Returns(blockWithMissingBody);
        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            using ReceiptsMessage70 response = new(sent.RequestId, new[] { receipts }.ToPooledList(), false);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        using IOwnedReadOnlyList<TxReceipt[]> result = await _handler.GetReceipts(new[] { blockHash }, CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(1));
        AssertReceiptsEqual(result[0], receipts);
    }

    [Test]
    public void Should_reject_receipts_when_transaction_count_mismatches()
    {
        Hash256 blockHash = TestItem.KeccakA;
        TxReceipt[] receipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] }
        ];
        SetupBlockMetadata(blockHash, 1_000_000, GasCostOf.Transaction, 100_000, 100_000);

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            using ReceiptsMessage70 response = new(sent.RequestId, new[] { receipts }.ToPooledList(), false);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        Func<Task> act = async () => await _handler.GetReceipts(new[] { blockHash }, CancellationToken.None);

        SubprotocolException? exception = Assert.ThrowsAsync<SubprotocolException>(async () => await act());
        Assert.That(exception?.Message, Is.EqualTo("Receipt count mismatch with block transactions count"));
    }

    [Test]
    public void Should_reject_receipt_larger_than_transaction_gas_limit_allows()
    {
        Hash256 blockHash = TestItem.KeccakA;
        TxReceipt[] receipts =
        [
            BuildReceiptWithLogData(GasCostOf.Transaction, logDataSize: 3_000)
        ];
        SetupBlockMetadata(blockHash, 1_000_000, GasCostOf.Transaction, GasCostOf.Transaction);

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            using ReceiptsMessage70 response = new(sent.RequestId, new[] { receipts }.ToPooledList(), false);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        Func<Task> act = async () => await _handler.GetReceipts(new[] { blockHash }, CancellationToken.None);

        SubprotocolException? exception = Assert.ThrowsAsync<SubprotocolException>(async () => await act());
        Assert.That(exception?.Message, Is.EqualTo("Receipt size exceeds transaction gas limit allowance"));
    }

    [Test]
    public void Should_reject_paginated_receipts_when_total_size_exceeds_block_gas_limit_allowance()
    {
        SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit = 16_000;

        Hash256 blockHash = TestItem.KeccakA;
        TxReceipt[] receipts =
        [
            BuildReceiptWithLogData(GasCostOf.Transaction, logDataSize: 5_000),
            BuildReceiptWithLogData(GasCostOf.Transaction * 2, logDataSize: 5_000)
        ];
        SetupBlockMetadata(blockHash, 80_000, GasCostOf.Transaction * 2, 1_000_000, 1_000_000);

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            TxReceipt[] payload = sent.FirstBlockReceiptIndex == 0
                ? [receipts[0]]
                : [receipts[1]];

            using ReceiptsMessage70 response = new(sent.RequestId, new[] { payload }.ToPooledList(), sent.FirstBlockReceiptIndex == 0);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        Func<Task> act = async () => await _handler.GetReceipts(new[] { blockHash }, CancellationToken.None);

        SubprotocolException? exception = Assert.ThrowsAsync<SubprotocolException>(async () => await act());
        Assert.That(exception?.Message, Is.EqualTo("Block receipts size exceeds block gas limit allowance"));
    }

    [TestCase(false, false, 1, 2, "Block gas used exceeds header value")]
    [TestCase(true, false, 1, 2, "Block gas used exceeds header value")]
    [TestCase(false, true, 1, 2, null)]
    [TestCase(true, true, 1, 2, null)]
    [TestCase(false, false, 2, 1, "Block gas used mismatch between receipts and header")]
    [TestCase(true, false, 2, 1, null)]
    public async Task Should_validate_receipt_cumulative_against_header_gas_used_by_spec(
        bool isEip7778Enabled,
        bool isEip8037Enabled,
        int headerGasUsedMultiplier,
        int receiptGasUsedMultiplier,
        string? expectedException)
    {
        Hash256 blockHash = TestItem.KeccakA;
        BlockHeader header = Build.A.BlockHeader
            .WithHash(blockHash)
            .WithNumber(1)
            .WithGasUsed(GasCostOf.Transaction * (ulong)headerGasUsedMultiplier)
            .TestObject;
        _syncManager.FindHeader(blockHash).Returns(header);

        IReleaseSpec spec = ReleaseSpecSubstitute.Create();
        spec.IsEip7778Enabled.Returns(isEip7778Enabled);
        spec.IsEip8037Enabled.Returns(isEip8037Enabled);
        _specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(spec);

        TxReceipt[] receipts = new TxReceipt[receiptGasUsedMultiplier];
        for (int i = 0; i < receipts.Length; i++)
        {
            receipts[i] = new TxReceipt { GasUsedTotal = GasCostOf.Transaction * (ulong)(i + 1), Logs = [] };
        }

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];
            using ReceiptsMessage70 response = new(sent.RequestId, new[] { receipts }.ToPooledList(), false);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        async Task Act()
        {
            using IOwnedReadOnlyList<TxReceipt[]> result = await _handler.GetReceipts(new[] { blockHash }, CancellationToken.None);
            Assert.That(result, Has.Count.EqualTo(1));
        }

        if (expectedException is null)
        {
            await Act();
        }
        else
        {
            SubprotocolException? exception = Assert.ThrowsAsync<SubprotocolException>(async () => await Act());
            Assert.That(exception?.Message, Is.EqualTo(expectedException));
        }
    }

    [Test]
    public void Should_reject_null_receipt_payload_during_deserialization()
    {
        TxReceipt[] receipts = [null!];
        using ReceiptsMessage70 response = new(1111, new[] { receipts }.ToPooledList(), false);

        HandleIncomingStatusMessage();
        RlpException? exception = Assert.Throws<RlpException>(() => HandleZeroMessage(response, Eth70MessageCode.Receipts));

        Assert.That(exception?.Message, Is.EqualTo("Unexpected null receipt payload"));
    }

    [Test]
    public async Task Should_return_immediately_when_peer_returns_fewer_blocks_than_requested()
    {
        // Scenario: Handler requests N blocks, peer returns fewer with LastBlockIncomplete = false
        // Expected: Handler should NOT loop and request more, but return immediately

        const int totalBlocks = 10;

        Hash256[] blockHashes = Enumerable.Range(0, totalBlocks)
            .Select(i => new Hash256(Keccak.Compute(BitConverter.GetBytes(i)).Bytes))
            .ToArray();

        int requestCount = 0;

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage70>())).Do(call =>
        {
            requestCount++;
            GetReceiptsMessage70 sent = (GetReceiptsMessage70)call[0];

            // Return only 7 blocks when more were requested, with LastBlockIncomplete = false
            // This simulates peer stopping early (e.g., doesn't have remaining blocks)
            int actualRequested = sent.Hashes.Count;
            int toReturn = Math.Min(7, actualRequested);

            TxReceipt[][] payload = Enumerable.Range(0, toReturn)
                .Select(i => new[] { new TxReceipt { GasUsedTotal = GasCostOf.Transaction * (ulong)(i + 1), Logs = [] } })
                .ToArray();

            ReceiptsMessage70 response = new(sent.RequestId, payload.ToPooledList(), lastBlockIncomplete: false);
            HandleZeroMessage(response, Eth70MessageCode.Receipts);
        });

        HandleIncomingStatusMessage();
        using IOwnedReadOnlyList<TxReceipt[]> result = await _handler.GetReceipts(blockHashes, CancellationToken.None);

        // Handler should make only 1 request - not loop trying to get remaining 3 blocks
        // When peer returns fewer than requested with incomplete=false, it means peer can't/won't provide more
        Assert.That(requestCount, Is.EqualTo(1), "handler should not loop requesting more when peer returned fewer with incomplete=false");
        Assert.That(result, Has.Count.EqualTo(7), "should return what peer gave us without looping");
    }

    [Test]
    public void Should_return_multiple_blocks_with_empty_receipts_in_between()
    {
        TxReceipt[] block1Receipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] },
            new() { GasUsedTotal = GasCostOf.Transaction * 2, Logs = [] }
        ];

        TxReceipt[] block3Receipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] }
        ];

        using GetReceiptsMessage70 request = new(1111, 0, new[] { Keccak.Zero, TestItem.KeccakA, TestItem.KeccakB }.ToPooledList());

        _syncManager.GetReceipts(Keccak.Zero).Returns(block1Receipts);
        _syncManager.GetReceipts(TestItem.KeccakA).Returns(Array.Empty<TxReceipt>());
        _syncManager.GetReceipts(TestItem.KeccakB).Returns(block3Receipts);

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        _session.Received().DeliverMessage(Arg.Is<ReceiptsMessage70>(m =>
            m.TxReceipts.Count == 3 &&
            m.TxReceipts[0].Length == 2 &&
            m.TxReceipts[1].Length == 0 &&
            m.TxReceipts[2].Length == 1 &&
            !m.LastBlockIncomplete));
    }

    [Test]
    public void Should_handle_first_block_with_empty_receipts()
    {
        TxReceipt[] block2Receipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] }
        ];

        using GetReceiptsMessage70 request = new(1111, 0, new[] { Keccak.Zero, TestItem.KeccakA }.ToPooledList());

        _syncManager.GetReceipts(Keccak.Zero).Returns(Array.Empty<TxReceipt>());
        _syncManager.GetReceipts(TestItem.KeccakA).Returns(block2Receipts);

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        _session.Received().DeliverMessage(Arg.Is<ReceiptsMessage70>(m =>
            m.TxReceipts.Count == 2 &&
            m.TxReceipts[0].Length == 0 &&
            m.TxReceipts[1].Length == 1 &&
            !m.LastBlockIncomplete));
    }

    [Test]
    public void Should_stop_receipts_response_at_first_unknown_block_hash()
    {
        TxReceipt[] block1Receipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] }
        ];

        _syncManager.GetReceipts(Keccak.Zero).Returns(block1Receipts);
        _syncManager.GetReceipts(TestItem.KeccakA).Returns((TxReceipt[]?)null);
        _syncManager.GetReceipts(TestItem.KeccakB).Returns(
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] }
        ]);

        ReceiptsMessage70 response = RequestReceipts(Keccak.Zero, TestItem.KeccakA, TestItem.KeccakB);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.TxReceipts, Has.Count.EqualTo(1));
            Assert.That(response.TxReceipts[0], Has.Length.EqualTo(block1Receipts.Length));
            Assert.That(response.TxReceipts[0][0].GasUsedTotal, Is.EqualTo(block1Receipts[0].GasUsedTotal));
            Assert.That(response.LastBlockIncomplete, Is.False);
        }
    }

    [Test]
    public void Should_return_empty_receipts_response_when_first_hash_is_unknown()
    {
        _syncManager.GetReceipts(Keccak.Zero).Returns((TxReceipt[]?)null);

        ReceiptsMessage70 response = RequestReceipts(Keccak.Zero, TestItem.KeccakA);

        Assert.That(response.TxReceipts, Is.Empty);
        Assert.That(response.LastBlockIncomplete, Is.False);
    }

    [Test]
    public void Should_disconnect_when_first_block_receipt_index_nonzero_for_empty_block()
    {
        using GetReceiptsMessage70 request = new(1111, 5, new[] { Keccak.Zero }.ToPooledList());
        _syncManager.GetReceipts(Arg.Any<Hash256>()).Returns(Array.Empty<TxReceipt>());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        _session.Received().InitiateDisconnect(DisconnectReason.BackgroundTaskFailure,
            Arg.Is<string>(s => s.Contains("Invalid firstBlockReceiptIndex")));
    }

    [Test]
    public void Should_truncate_large_block_in_middle()
    {
        SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit = 300;

        TxReceipt[] block1Receipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [new LogEntry(TestItem.AddressA, new byte[100], [])] },
                new() { GasUsedTotal = GasCostOf.Transaction * 2, Logs = [new LogEntry(TestItem.AddressA, new byte[100], [])] },
                new() { GasUsedTotal = GasCostOf.Transaction * 3, Logs = [new LogEntry(TestItem.AddressA, new byte[100], [])] }
        ];

        using ReceiptsMessage70 twoReceiptsResponse = new(1111, new[] { block1Receipts.Take(2).ToArray() }.ToPooledList(), lastBlockIncomplete: true);
        SyncPeerProtocolHandlerBase.HardOutgoingReceiptsMessageSizeLimit = (ulong)(GetReceiptsMessageLength(twoReceiptsResponse) - 1);

        using GetReceiptsMessage70 request = new(1111, 0, new[] { Keccak.Zero }.ToPooledList());

        _syncManager.GetReceipts(Keccak.Zero).Returns(block1Receipts);

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        _session.Received().DeliverMessage(Arg.Is<ReceiptsMessage70>(m =>
            m.TxReceipts.Count == 1 &&
            m.TxReceipts[0].Length == 1 &&
            m.LastBlockIncomplete));
    }

    [Test]
    public void Should_stop_before_second_block_when_cumulative_exceeds_soft_limit()
    {
        TxReceipt[] block1Receipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction * 50, Logs = [new LogEntry(TestItem.AddressA, new byte[44], [])] },
                new() { GasUsedTotal = GasCostOf.Transaction * 100, Logs = [new LogEntry(TestItem.AddressA, new byte[44], [])] }
        ];

        TxReceipt[] block2Receipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] },
            new() { GasUsedTotal = GasCostOf.Transaction * 2, Logs = [] }
        ];

        using ReceiptsMessage70 twoBlocksResponse = new(1111, new[] { block1Receipts, block2Receipts }.ToPooledList(), lastBlockIncomplete: false);
        SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit = (ulong)(GetReceiptsMessageLength(twoBlocksResponse) - 1);

        _syncManager.GetReceipts(Keccak.Zero).Returns(block1Receipts);
        _syncManager.GetReceipts(TestItem.KeccakA).Returns(block2Receipts);

        using GetReceiptsMessage70 request = new(1111, 0, new[] { Keccak.Zero, TestItem.KeccakA }.ToPooledList());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        _session.Received().DeliverMessage(Arg.Is<ReceiptsMessage70>(m =>
            m.TxReceipts.Count == 1 &&
            m.TxReceipts[0].Length == 2 &&
            !m.LastBlockIncomplete));
    }

    [Test]
    public void Should_include_next_block_after_empty_first_block_completion()
    {
        SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit = 1;
        SyncPeerProtocolHandlerBase.HardOutgoingReceiptsMessageSizeLimit = 1000;

        TxReceipt[] block1Receipts = BuildSequentialReceipts(1);
        TxReceipt[] block2Receipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [new LogEntry(TestItem.AddressA, new byte[100], [])] },
            new() { GasUsedTotal = GasCostOf.Transaction * 2, Logs = [new LogEntry(TestItem.AddressA, new byte[100], [])] }
        ];

        _syncManager.GetReceipts(Keccak.Zero).Returns(block1Receipts);
        _syncManager.GetReceipts(TestItem.KeccakA).Returns(block2Receipts);

        using GetReceiptsMessage70 request = new(1111, block1Receipts.Length, new[] { Keccak.Zero, TestItem.KeccakA }.ToPooledList());

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        _session.Received().DeliverMessage(Arg.Is<ReceiptsMessage70>(m =>
            m.TxReceipts.Count == 2 &&
            m.TxReceipts[0].Length == 0 &&
            m.TxReceipts[1].Length == block2Receipts.Length &&
            !m.LastBlockIncomplete));
    }

    [Test]
    public void Should_send_single_large_block_above_soft_limit_when_below_hard_limit()
    {
        SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit = 300;
        SyncPeerProtocolHandlerBase.HardOutgoingReceiptsMessageSizeLimit = 1000;

        TxReceipt[] largeBlockReceipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [new LogEntry(TestItem.AddressA, new byte[100], [])] },
            new() { GasUsedTotal = GasCostOf.Transaction * 2, Logs = [new LogEntry(TestItem.AddressA, new byte[100], [])] }
        ];

        using GetReceiptsMessage70 request = new(1111, 0, new[] { Keccak.Zero }.ToPooledList());
        _syncManager.GetReceipts(Arg.Any<Hash256>()).Returns(largeBlockReceipts);

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        _session.Received().DeliverMessage(Arg.Is<ReceiptsMessage70>(m =>
            m.TxReceipts.Count == 1 &&
            m.TxReceipts[0].Length == largeBlockReceipts.Length &&
            !m.LastBlockIncomplete));
    }

    [Test]
    public void Should_count_rlp_wrappers_when_splitting_near_hard_limit()
    {
        SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit = 1;

        TxReceipt[] receipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] },
            new() { GasUsedTotal = GasCostOf.Transaction * 2, Logs = [] }
        ];

        using ReceiptsMessage70 fullResponse = new(1111, new[] { receipts }.ToPooledList(), lastBlockIncomplete: false);
        SyncPeerProtocolHandlerBase.HardOutgoingReceiptsMessageSizeLimit = (ulong)(GetReceiptsMessageLength(fullResponse) - 1);

        using GetReceiptsMessage70 request = new(1111, 0, new[] { Keccak.Zero }.ToPooledList());
        _syncManager.GetReceipts(Arg.Any<Hash256>()).Returns(receipts);

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        _session.Received().DeliverMessage(Arg.Is<ReceiptsMessage70>(m =>
            m.TxReceipts.Count == 1 &&
            m.TxReceipts[0].Length == 1 &&
            m.LastBlockIncomplete));
    }

    [Test]
    public void Should_count_actual_serialized_receipts_bytes_near_hard_limit()
    {
        SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit = 1;

        TxReceipt[] receipts =
        [
            new()
            {
                GasUsedTotal = GasCostOf.Transaction,
                Logs = Enumerable.Range(0, 20).Select(_ => new LogEntry(TestItem.AddressA, [], [])).ToArray()
            },
            new()
            {
                GasUsedTotal = GasCostOf.Transaction * 2,
                Logs = Enumerable.Range(0, 20).Select(_ => new LogEntry(TestItem.AddressA, [], [])).ToArray()
            }
        ];

        using ReceiptsMessage70 fullResponse = new(1111, new[] { receipts }.ToPooledList(), lastBlockIncomplete: false);
        int fullResponseLength = GetReceiptsMessageLength(fullResponse);
        SyncPeerProtocolHandlerBase.HardOutgoingReceiptsMessageSizeLimit = (ulong)(fullResponseLength - 1);

        _syncManager.GetReceipts(Keccak.Zero).Returns(receipts);

        ReceiptsMessage70 response = RequestReceipts(Keccak.Zero);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fullResponseLength, Is.GreaterThan((int)SyncPeerProtocolHandlerBase.HardOutgoingReceiptsMessageSizeLimit));
            Assert.That(response.TxReceipts, Has.Count.EqualTo(1));
            Assert.That(response.TxReceipts[0], Has.Length.EqualTo(1));
            Assert.That(response.LastBlockIncomplete, Is.True);
            Assert.That(GetReceiptsMessageLength(response), Is.LessThanOrEqualTo((int)SyncPeerProtocolHandlerBase.HardOutgoingReceiptsMessageSizeLimit));
        }
    }

    [Test]
    public void Should_split_large_block_when_exceeding_hard_limit()
    {
        SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit = 1000;
        SyncPeerProtocolHandlerBase.HardOutgoingReceiptsMessageSizeLimit = 1000;

        TxReceipt[] largeBlockReceipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [new LogEntry(TestItem.AddressA, new byte[400], [])] },
                new() { GasUsedTotal = GasCostOf.Transaction * 2, Logs = [new LogEntry(TestItem.AddressA, new byte[400], [])] },
                new() { GasUsedTotal = GasCostOf.Transaction * 3, Logs = [new LogEntry(TestItem.AddressA, new byte[400], [])] }
        ];

        using GetReceiptsMessage70 request = new(1111, 0, new[] { Keccak.Zero }.ToPooledList());
        _syncManager.GetReceipts(Arg.Any<Hash256>()).Returns(largeBlockReceipts);

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        _session.Received().DeliverMessage(Arg.Is<ReceiptsMessage70>(m =>
            m.TxReceipts.Count == 1 &&
            m.TxReceipts[0].Length < largeBlockReceipts.Length &&
            m.LastBlockIncomplete));
    }

    [Test]
    public void Should_not_split_small_block_when_hitting_limit_single_block()
    {
        SyncPeerProtocolHandlerBase.SoftOutgoingMessageSizeLimit = 10UL.MB;

        TxReceipt[] smallBlockReceipts =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] },
                new() { GasUsedTotal = GasCostOf.Transaction * 2, Logs = [] },
                new() { GasUsedTotal = GasCostOf.Transaction * 3, Logs = [] }
        ];

        using GetReceiptsMessage70 request = new(1111, 0, new[] { Keccak.Zero }.ToPooledList());
        _syncManager.GetReceipts(Arg.Any<Hash256>()).Returns(smallBlockReceipts);

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        // Small block that doesn't exceed limits - all receipts returned
        _session.Received().DeliverMessage(Arg.Is<ReceiptsMessage70>(m =>
            m.TxReceipts.Count == 1 &&
            m.TxReceipts[0].Length == smallBlockReceipts.Length &&
            !m.LastBlockIncomplete));
    }

    [Test]
    public async Task Handle_GetReceipts_disposes_request_message()
    {
        TrackableGetReceiptsMessage70 request = new(1111, 0, new[] { Keccak.Zero }.ToPooledList());
        _syncManager.GetReceipts(Arg.Any<Hash256>()).Returns(Array.Empty<TxReceipt>());

        using ReceiptsMessage70 response = await _handler.Handle(request, CancellationToken.None);

        Assert.That(request.IsDisposed, Is.True, "Handler must dispose the request message to release the pooled hash list");
    }

    private sealed class TrackableGetReceiptsMessage70(long requestId, long firstBlockReceiptIndex, IOwnedReadOnlyList<Hash256> hashes)
        : GetReceiptsMessage70(requestId, firstBlockReceiptIndex, hashes)
    {
        public bool IsDisposed { get; private set; }
        public override void Dispose() { IsDisposed = true; base.Dispose(); }
    }

    private static TxReceipt[] BuildSequentialReceipts(int count)
    {
        TxReceipt[] receipts = new TxReceipt[count];
        for (int i = 0; i < receipts.Length; i++)
        {
            receipts[i] = new TxReceipt { GasUsedTotal = GasCostOf.Transaction * (ulong)(i + 1), Logs = [] };
        }

        return receipts;
    }

    private static TxReceipt BuildReceiptWithLogData(ulong gasUsedTotal, int logDataSize) =>
        new()
        {
            GasUsedTotal = gasUsedTotal,
            Logs = [new LogEntry(TestItem.AddressA, new byte[logDataSize], [])]
        };

    private void SetupBlockMetadata(Hash256 blockHash, ulong gasLimit, ulong gasUsed, params ulong[] txGasLimits)
    {
        Transaction[] transactions = new Transaction[txGasLimits.Length];
        for (int i = 0; i < txGasLimits.Length; i++)
        {
            transactions[i] = Build.A.Transaction.WithGasLimit(txGasLimits[i]).TestObject;
        }

        BlockHeader header = Build.A.BlockHeader
            .WithHash(blockHash)
            .WithNumber(1)
            .WithGasLimit(gasLimit)
            .WithGasUsed(gasUsed)
            .TestObject;
        Block block = new(header, transactions, []);

        _syncManager.FindHeader(blockHash).Returns(header);
        _syncManager.Find(blockHash).Returns(block);
    }

    private sealed class CountingReadOnlyList<T>(IReadOnlyList<T> inner) : IReadOnlyList<T>
    {
        public int Count => inner.Count;

        public int IndexerReadCount { get; private set; }

        public T this[int index]
        {
            get
            {
                IndexerReadCount++;
                return inner[index];
            }
        }

        public IEnumerator<T> GetEnumerator() => inner.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void AssertReceiptsEqual(TxReceipt[] actual, TxReceipt[] expected) =>
        Assert.That(actual, Is.EqualTo(expected).UsingPropertiesComparer());

    private static IEnumerable<TestCaseData> SingleBlockReceiptResponseCases()
    {
        yield return new TestCaseData(0L, 3, 0, 2)
            .SetName("Should_return_receipts_when_first_block_receipt_index_is_zero");
        yield return new TestCaseData(2L, 1, 2, 2)
            .SetName("Should_return_last_receipt_when_first_block_receipt_index_is_last");
        yield return new TestCaseData(3L, 0, null, null)
            .SetName("Should_return_empty_block_when_first_block_receipt_index_equals_receipts_count");
    }

    public sealed record InvalidPartialContinuationCase(
        Hash256[] RequestedHashes,
        Dictionary<long, ReceiptsPageResponse> Responses,
        ulong FirstBlockGasUsed,
        ulong? SecondBlockGasUsed,
        string ExpectedExceptionMessage);

    public sealed record ReceiptsPageResponse(TxReceipt[][] Receipts, bool LastBlockIncomplete);

    private static IEnumerable<TestCaseData> InvalidPartialContinuationCases()
    {
        TxReceipt[] firstShortPage =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] }
        ];

        TxReceipt[] finalShortPage =
        [
            new() { GasUsedTotal = GasCostOf.Transaction * 2, Logs = [] }
        ];

        TxReceipt[] nextBlock =
        [
            new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] }
        ];

        yield return new TestCaseData(new InvalidPartialContinuationCase(
            [Keccak.Zero, TestItem.KeccakA],
            new Dictionary<long, ReceiptsPageResponse>
            {
                [0] = new([firstShortPage], LastBlockIncomplete: true),
                [1] = new([finalShortPage, nextBlock], LastBlockIncomplete: false)
            },
            FirstBlockGasUsed: GasCostOf.Transaction * 3,
            SecondBlockGasUsed: GasCostOf.Transaction,
            ExpectedExceptionMessage: "Block gas used mismatch between receipts and header"))
            .SetName("Rejects partial continuation that completes below header gas before continuing");

        yield return new TestCaseData(new InvalidPartialContinuationCase(
            [Keccak.Zero, TestItem.KeccakA],
            new Dictionary<long, ReceiptsPageResponse>
            {
                [0] = new([firstShortPage], LastBlockIncomplete: true),
                [1] = new([Array.Empty<TxReceipt>(), nextBlock], LastBlockIncomplete: false)
            },
            FirstBlockGasUsed: GasCostOf.Transaction * 2,
            SecondBlockGasUsed: GasCostOf.Transaction,
            ExpectedExceptionMessage: "Block gas used mismatch between receipts and header"))
            .SetName("Rejects empty partial continuation that completes below header gas before continuing");
    }

    private static IEnumerable<TestCaseData> InvalidFirstBlockReceiptIndexCases()
    {
        yield return new TestCaseData(3L)
            .SetName("Should_disconnect_when_first_block_receipt_index_exceeds_receipts_count");
    }

    private int GetReceiptsMessageLength(ReceiptsMessage70 message) =>
        new ReceiptsMessageSerializer70(_specProvider).GetLength(message, out _);

    public enum EmptyReceiptsPayloadScenario
    {
        FirstComplete,
        FirstIncomplete,
        PartialContinuationComplete
    }

    private static IEnumerable<TestCaseData> EmptyReceiptsPayloadCases()
    {
        yield return new TestCaseData(EmptyReceiptsPayloadScenario.FirstComplete, null, 1, 0)
            .SetName("Should_accept_empty_receipts_payload_when_requesting_from_peer");
        yield return new TestCaseData(EmptyReceiptsPayloadScenario.FirstIncomplete, "Peer returned no progress for partial receipts", 1, 0)
            .SetName("Should_reject_empty_incomplete_receipts_payload_when_requesting_from_peer");
        yield return new TestCaseData(EmptyReceiptsPayloadScenario.PartialContinuationComplete, "Peer returned no progress for partial receipts", 2, 0)
            .SetName("Should_reject_empty_receipts_payload_for_partial_continuation");
    }

    private static ReceiptsMessage70 BuildEmptyReceiptsPayloadResponse(
        GetReceiptsMessage70 sent,
        EmptyReceiptsPayloadScenario scenario,
        TxReceipt[] firstPage)
    {
        if (scenario == EmptyReceiptsPayloadScenario.PartialContinuationComplete && sent.FirstBlockReceiptIndex == 0)
        {
            return new ReceiptsMessage70(sent.RequestId, new[] { firstPage }.ToPooledList(), lastBlockIncomplete: true);
        }

        bool lastBlockIncomplete = scenario == EmptyReceiptsPayloadScenario.FirstIncomplete;
        return new ReceiptsMessage70(sent.RequestId, ArrayPoolList<TxReceipt[]>.Empty(), lastBlockIncomplete);
    }

    private void HandleIncomingStatusMessage()
    {
        using StatusMessage69 statusMsg = new() { ProtocolVersion = 70, GenesisHash = _genesisBlock.Hash!, LatestBlockHash = _genesisBlock.Hash! };

        using DisposableByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg).AsDisposable();
        statusPacket.ReadByte();
        _handler.HandleMessage(new ZeroPacket(statusPacket) { PacketType = 0 });
    }

    private void HandleZeroMessage<T>(T msg, int messageCode) where T : MessageBase
    {
        using DisposableByteBuffer packet = _svc!.ZeroSerialize(msg).AsDisposable();
        packet.ReadByte();
        _handler!.HandleMessage(new ZeroPacket(packet) { PacketType = (byte)messageCode });
    }

    private ReceiptsMessage70 RequestReceipts(params Hash256[] hashes)
    {
        using GetReceiptsMessage70 request = new(1111, 0, hashes.ToPooledList());
        ReceiptsMessage70? response = null;
        _session.When(session => session.DeliverMessage(Arg.Any<ReceiptsMessage70>()))
            .Do(call => response = (ReceiptsMessage70)call[0]);

        HandleIncomingStatusMessage();
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        Assert.That(response, Is.Not.Null);
        return response!;
    }
}
