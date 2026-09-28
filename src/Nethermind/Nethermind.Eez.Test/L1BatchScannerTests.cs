// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Eez.Follower;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class L1BatchScannerTests
{
    private const ulong RollupId = 1;
    private const ulong BlockNumber = 76;
    private const ulong TransactionIndex = 2;
    private static readonly Address Registry = new("0x5fbdb2315678afecb367f032d93f642f64180aa3");
    private static readonly Hash256 BlockHash = Keccak.Compute("L1 block 76");
    private static readonly Hash256 TransactionHash = Keccak.Compute("postAndVerifyBatch");

    private IEezL1Api _l1 = null!;
    private byte[] _recordedBatch = null!;

    [SetUp]
    public void SetUp()
    {
        _l1 = Substitute.For<IEezL1Api>();
        _recordedBatch = StatelessFixtures.ReadPostBatch("captured-devnet-window-438");
        Transaction(TransactionIndex, TransactionHash, _recordedBatch);
    }

    /// <summary>
    /// The batch L1 accepted on the devnet, read back the way the follower reads it:
    /// 1. its <c>BatchPosted</c> log leads to the transaction by block hash and index;
    /// 2. its four <c>L2ExecutionPerformed</c> logs settle the anchor and the three effects.
    /// </summary>
    [Test]
    public async Task Scan_RecordedDevnetBatch_SettlesItWhole()
    {
        ValueHash256[] claimed = ClaimedChain(_recordedBatch);
        Logs(L1BatchScanner.BatchPostedTopic, BatchPosted(TransactionIndex, TransactionHash));
        Logs(L1BatchScanner.L2ExecutionPerformedTopic, claimed.Select(static (root, i) => Settled(TransactionIndex, TransactionHash, (ulong)i, root)).ToArray());

        ScannedBatch[] scanned = await Scanner().Scan(BlockNumber, BlockNumber);

        Assert.That(scanned, Has.Length.EqualTo(1), "one batch in the block");
        Assert.That(scanned[0].Settlement, Is.EqualTo(new L1Settlement(0, claimed.Length, claimed[^1], scanned[0].Batch.ClaimedCurrentState!.Value)),
            "every claimed step ran, ending at the recorded window's last block");
        Assert.That(scanned[0].Settlement.Effects, Is.EqualTo(new ProducingSlice(0, claimed.Length - 1)), "the three effects all applied");
    }

    [Test]
    public async Task Scan_NodeRefusesTheRange_HalvesUntilItServesIt()
    {
        _l1.GetLogs(Registry, L1BatchScanner.BatchPostedTopic, null, Arg.Any<ulong>(), Arg.Any<ulong>())
            .Returns(call => call.ArgAt<ulong>(3) == call.ArgAt<ulong>(4)
                ? [BatchPosted(TransactionIndex, TransactionHash, call.ArgAt<ulong>(3)) with { BlockHash = Keccak.Compute($"L1 block {call.ArgAt<ulong>(3)}") }]
                : null);
        _l1.GetTransactionByBlockHashAndIndex(Arg.Any<Hash256>(), TransactionIndex)
            .Returns(new EezL1Transaction { Hash = TransactionHash, Input = _recordedBatch });
        Logs(L1BatchScanner.L2ExecutionPerformedTopic);

        ScannedBatch[] scanned = await Scanner().Scan(10, 13);

        Assert.That(scanned.Select(static s => s.Batch.BlockNumber), Is.EqualTo(new ulong[] { 10, 11, 12, 13 }), "every block of the refused range is read, in order");
        Assert.That(scanned.All(static s => s.Settlement.IsEmpty), Is.True, "nothing settled our rollup");
    }

    [Test]
    public void Scan_NodeRefusesOneBlock_Throws()
    {
        _l1.GetLogs(Registry, L1BatchScanner.BatchPostedTopic, null, BlockNumber, BlockNumber).Returns((EezL1Log[]?)null);

        Assert.ThrowsAsync<L1SourceIncompleteException>(() => Scanner().Scan(BlockNumber, BlockNumber), "a block that cannot be served is retried later");
    }

    [Test]
    public void Scan_TransactionDiffersFromItsLog_Throws()
    {
        Logs(L1BatchScanner.BatchPostedTopic, BatchPosted(TransactionIndex, Keccak.Compute("replaced by a reorg")));
        Logs(L1BatchScanner.L2ExecutionPerformedTopic);

        Assert.ThrowsAsync<L1SourceIncompleteException>(() => Scanner().Scan(BlockNumber, BlockNumber), "L1 reorganized between the log and the transaction read");
    }

    [Test]
    public async Task Scan_ForeignBatchThatDoesNotDecode_IsSkipped()
    {
        Hash256 foreign = Keccak.Compute("router call");
        Transaction(TransactionIndex + 1, foreign, [0xde, 0xad]);
        Logs(L1BatchScanner.BatchPostedTopic, BatchPosted(TransactionIndex + 1, foreign));
        Logs(L1BatchScanner.L2ExecutionPerformedTopic);

        ScannedBatch[] scanned = await Scanner().Scan(BlockNumber, BlockNumber);

        Assert.That(scanned, Is.Empty, "a batch that did not settle our rollup cannot stop the follower");
    }

    [Test]
    public void Scan_UndecodableBatchThatSettledUs_Throws()
    {
        Hash256 ours = Keccak.Compute("router call settling us");
        Transaction(TransactionIndex + 1, ours, [0xde, 0xad]);
        Logs(L1BatchScanner.BatchPostedTopic, BatchPosted(TransactionIndex + 1, ours));
        Logs(L1BatchScanner.L2ExecutionPerformedTopic, Settled(TransactionIndex + 1, ours, 0, Keccak.Compute("root")));

        Assert.ThrowsAsync<EezFollowerException>(() => Scanner().Scan(BlockNumber, BlockNumber), "L1 settled our rollup with a batch the follower cannot derive");
    }

    [Test]
    public void Deserialize_EthGetLogsResponse_ReadsHexFields()
    {
        const string json = """
            [{"address":"0x5fbdb2315678afecb367f032d93f642f64180aa3","topics":["0x0000000000000000000000000000000000000000000000000000000000000001"],
              "data":"0x0a0b","blockNumber":"0x4c","blockHash":"0x5687285fd77b207c03e1239a6c7dd182423df7a4a9fc608158cba29ea9a7068d",
              "transactionHash":"0x5a9fa55a5305398ab86ac57741a4a070f50925d7f967b6cd905c7ce9987aa600","transactionIndex":"0x2","logIndex":"0x3","removed":false}]
            """;

        EezL1Log log = new EthereumJsonSerializer().Deserialize<EezL1Log[]>(json)!.Single();

        Assert.That((log.Address, log.BlockNumber, log.TransactionIndex, log.LogIndex, Convert.ToHexString(log.Data)),
            Is.EqualTo((Registry, 76UL, 2UL, 3UL, "0A0B")), "the node's hex quantities and data decode into the log");
    }

    private L1BatchScanner Scanner() => new(_l1, Registry, RollupId, LimboLogs.Instance);

    private void Logs(Hash256 topic, params EezL1Log[] logs) =>
        _l1.GetLogs(Registry, topic, Arg.Any<Hash256?>(), Arg.Any<ulong>(), Arg.Any<ulong>()).Returns(logs);

    private void Transaction(ulong index, Hash256 hash, byte[] input) =>
        _l1.GetTransactionByBlockHashAndIndex(BlockHash, index).Returns(new EezL1Transaction { Hash = hash, Input = input });

    private static ValueHash256[] ClaimedChain(byte[] calldata) =>
        L1Batch.Of(EezCalldata.DecodePostAndVerifyBatch(calldata), RollupId, BlockNumber, BlockHash, TransactionHash, TransactionIndex).ClaimedChain;

    private static EezL1Log BatchPosted(ulong index, Hash256 transaction, ulong block = BlockNumber) => new()
    {
        Address = Registry,
        Topics = [L1BatchScanner.BatchPostedTopic, Keccak.Zero],
        Data = [],
        BlockNumber = block,
        BlockHash = BlockHash,
        TransactionHash = transaction,
        TransactionIndex = index,
    };

    private static EezL1Log Settled(ulong index, Hash256 transaction, ulong logIndex, in ValueHash256 root) => new()
    {
        Address = Registry,
        Topics = [L1BatchScanner.L2ExecutionPerformedTopic],
        Data = root.ToByteArray(),
        BlockNumber = BlockNumber,
        BlockHash = BlockHash,
        TransactionHash = transaction,
        TransactionIndex = index,
        LogIndex = logIndex,
    };
}
