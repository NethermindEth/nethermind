// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.Test;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

/// <summary>
/// Regression for receipt loss across a capture breaker trip: once the trip lets the persist resume and prune the
/// blocks' regeneration sources, a restarted node must still serve the bodies skipped since the last watermark.
/// </summary>
[TestFixture]
public class ReceiptRetentionAcrossCaptureBreakerTests
{
    private const int MaxConsecutiveCaptureFailures = 16;

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private ResourcePool _resourcePool = null!;
    private FlatTestContainer _tier = null!;
    private HistoryWriter _writer = null!;

    private TestSpecProvider _specProvider = null!;
    private TestMemColumnsDb<ReceiptsColumns> _receiptsDb = null!;
    private ReceiptConfig _receiptConfig = null!;
    private IBlockTree _blockTree = null!;
    private IBlockStore _blockStore = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _resourcePool = new ResourcePool(new FlatDbConfig { CompactSize = 16 });
        _tier = new FlatTestContainer(new FlatDbConfig { CompactSize = 16 });
        _writer = new HistoryWriter(_db, _historyColumns, new FlatDbConfig { HistoryEnabled = true }, LimboLogs.Instance);

        _specProvider = new TestSpecProvider(Byzantium.Instance);
        _receiptsDb = new TestMemColumnsDb<ReceiptsColumns>();
        // A hash-valued tx index resolves without the block tree.
        _receiptConfig = new ReceiptConfig { DeriveFromState = true, CompactTxIndex = false };
        _blockTree = Substitute.For<IBlockTree>();
        _blockStore = Substitute.For<IBlockStore>();
    }

    [TearDown]
    public void TearDown()
    {
        _tier.Dispose();
        _db.Dispose();
        _historyColumns.Dispose();
        _receiptsDb.Dispose();
    }

    [Test]
    public void Bodies_skipped_before_a_breaker_trip_survive_restart_and_serve_transaction_lookups()
    {
        PersistentReceiptStorage storage = CreateStorage();
        _writer.SeedGenesis([], StateAt(0).StateRoot);
        CommitBlock(0, 1);
        _writer.CaptureUpTo(StateAt(1), _tier.Repository, CancellationToken.None);
        Assert.That(_writer.CaptureHealthy, Is.True, "precondition: only a proven capture skips bodies");

        Block block = ProcessedBlock();
        storage.InsertDeferred(block, [Build.A.Receipt.WithCalculatedBloom().TestObject], _specProvider.GetSpec((ForkActivation)block.Number));
        storage.EnsureCanonical(block);
        Assert.That(CreateStorage().HasBlock(block.Number, block.Hash!), Is.False,
            "precondition: the body write is skipped while capture is healthy");

        // Block 2 will never be captured, so the trip must persist its retained body.
        ISnapshotRepository failing = Substitute.For<ISnapshotRepository>();
        failing.TryLeaseInMemoryState(default, default, out _).ThrowsForAnyArgs(new IOException("disk failure"));
        for (int i = 0; i < MaxConsecutiveCaptureFailures; i++)
        {
            Assert.Throws<IOException>(() => _writer.CaptureUpTo(StateAt(2), failing, CancellationToken.None));
        }

        // "Restart": a fresh storage over the same database.
        PersistentReceiptStorage restarted = CreateStorage();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(restarted.HasBlock(block.Number, block.Hash!), Is.True,
                "the retained body must be durable after the trip");
            Assert.That(restarted.FindBlockHash(block.Transactions[0].Hash!), Is.EqualTo(block.Hash),
                "the transaction lookup must resolve the block");
            Assert.That(restarted.Get(block), Has.Length.EqualTo(1), "the receipt body must be servable");
        }
    }

    [Test]
    public void Bodies_covered_by_the_watermark_are_not_written_back_at_the_trip()
    {
        PersistentReceiptStorage storage = CreateStorage();
        _writer.SeedGenesis([], StateAt(0).StateRoot);
        CommitBlock(0, 1);
        _writer.CaptureUpTo(StateAt(1), _tier.Repository, CancellationToken.None);

        Block block = ProcessedBlock();
        storage.InsertDeferred(block, [Build.A.Receipt.WithCalculatedBloom().TestObject], _specProvider.GetSpec((ForkActivation)block.Number));

        // Capture catches up over block 2, making it durably derivable, before the breaker trips.
        CommitBlock(1, 2);
        _writer.CaptureUpTo(StateAt(2), _tier.Repository, CancellationToken.None);
        ISnapshotRepository failing = Substitute.For<ISnapshotRepository>();
        failing.TryLeaseInMemoryState(default, default, out _).ThrowsForAnyArgs(new IOException("disk failure"));
        for (int i = 0; i < MaxConsecutiveCaptureFailures; i++)
        {
            Assert.Throws<IOException>(() => _writer.CaptureUpTo(StateAt(3), failing, CancellationToken.None));
        }

        Assert.That(CreateStorage().HasBlock(block.Number, block.Hash!), Is.False,
            "a derivable body must stay skipped after the trip");
    }

    private PersistentReceiptStorage CreateStorage() => new(
        _receiptsDb,
        _specProvider,
        new ReceiptsRecovery(new EthereumEcdsa(_specProvider.ChainId), _specProvider),
        _blockTree,
        _blockStore,
        _receiptConfig,
        historyCaptureStatus: _writer)
    { MigratedBlockNumber = 0 };

    private static Block ProcessedBlock() => Build.A.Block
        .WithNumber(2)
        .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject)
        .WithReceiptsRoot(TestItem.KeccakA)
        .TestObject;

    private void CommitBlock(ulong fromBlock, ulong toBlock)
    {
        Snapshot snapshot = _resourcePool.CreateSnapshot(StateAt(fromBlock), StateAt(toBlock), ResourcePool.Usage.ReadOnlyProcessingEnv);
        snapshot.Content.Accounts[TestItem.AddressA] = new Account(toBlock, toBlock);
        Assert.That(_tier.Repository.TryAdd(snapshot, SnapshotTier.InMemoryBase), Is.True);
        _tier.Repository.AddStateId(StateAt(toBlock));
    }

    private static StateId StateAt(ulong blockNumber)
    {
        Span<byte> root = stackalloc byte[32];
        root[0] = (byte)blockNumber;
        return new StateId(blockNumber, new ValueHash256(root));
    }
}
