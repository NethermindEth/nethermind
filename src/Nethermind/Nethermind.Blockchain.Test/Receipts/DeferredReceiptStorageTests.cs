// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Encoding;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts;

[TestFixture(true)]
[TestFixture(false)]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class DeferredReceiptStorageTests(bool useCompactReceipts)
{
    private readonly TestSpecProvider _specProvider = new(Byzantium.Instance);
    private TestMemColumnsDb<ReceiptsColumns> _receiptsDb = null!;
    private ReceiptsRecovery _receiptsRecovery = null!;
    private IBlockTree _blockTree = null!;
    private IBlockStore _blockStore = null!;
    private ReceiptConfig _receiptConfig = null!;
    private ReceiptArrayStorageDecoder _decoder = null!;
    private StatePersistenceBarrier _barrier = null!;
    private DeferredBlockDataWriter _writer = null!;
    private PersistentReceiptStorage _storage = null!;

    [SetUp]
    public void SetUp()
    {
        EthereumEcdsa ethereumEcdsa = new(_specProvider.ChainId);
        _receiptConfig = new ReceiptConfig();
        _receiptsRecovery = new(ethereumEcdsa, _specProvider);
        _receiptsDb = new TestMemColumnsDb<ReceiptsColumns>();
        _blockTree = Substitute.For<IBlockTree>();
        _blockStore = Substitute.For<IBlockStore>();
        _decoder = new ReceiptArrayStorageDecoder(useCompactReceipts);
        _barrier = new StatePersistenceBarrier();
        _writer = DeferredWriteTestHelpers.ManualWriter(_barrier);
        _storage = CreateStorage(_writer, _barrier);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _writer.DisposeAsync();
        _receiptsDb.Dispose();
    }

    private PersistentReceiptStorage CreateStorage(DeferredBlockDataWriter? writer, IStatePersistenceBarrier? barrier = null) =>
        new(_receiptsDb, _specProvider, _receiptsRecovery, _blockTree, _blockStore, _receiptConfig, _decoder, writer, barrier)
        { MigratedBlockNumber = 0 };

    // Receipts as read by a fresh store over the same database - durable state independent of any overlay.
    private TxReceipt[] DurableReceipts(Block block) => CreateStorage(null).Get(block);

    private (Block block, TxReceipt[] receipts) PrepareBlock()
    {
        Block block = Build.A.Block
            .WithNumber(1)
            .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject)
            .WithReceiptsRoot(TestItem.KeccakA)
            .TestObject;

        _blockTree.FindBlock(block.Hash!).Returns(block);
        _blockTree.FindBlockHash(block.Number).Returns(block.Hash);
        _blockTree.FindBestSuggestedHeader().Returns(block.Header);

        TxReceipt[] receipts = [Build.A.Receipt.WithCalculatedBloom().TestObject];
        return (block, receipts);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Deferred_insert_is_visible_before_flush()
    {
        (Block block, TxReceipt[] receipts) = PrepareBlock();
        _storage.InsertDeferred(block, receipts, _specProvider.GetSpec(block.Header));

        // Kill the LRU so only the pending overlay can serve.
        _storage.ClearCache();

        Assert.That(_storage.HasBlock(block.Number, block.Hash!), Is.True);
        _storage.Get(block).AssertEquivalentTo(receipts, nameof(TxReceipt.Error));

        Assert.That(_storage.TryGetReceiptsIterator(block.Number, block.Hash!, out ReceiptsIterator iterator), Is.True);
        Assert.That(iterator.TryGetNext(out _), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Deferred_insert_is_durable_after_flush()
    {
        (Block block, TxReceipt[] receipts) = PrepareBlock();
        _storage.InsertDeferred(block, receipts, _specProvider.GetSpec(block.Header));

        _writer.Pump();

        // A fresh storage over the same database (no writer) proves durability.
        PersistentReceiptStorage reopened = CreateStorage(null);
        reopened.Get(block).AssertEquivalentTo(receipts, nameof(TxReceipt.Error));

        Span<byte> key = stackalloc byte[40];
        block.Number.WriteBigEndian(key);
        block.Hash!.Bytes.CopyTo(key[8..]);
        TestMemDb receiptsDb = (TestMemDb)_receiptsDb.GetColumnDb(ReceiptsColumns.Blocks);
        receiptsDb.KeyWasWrittenWithFlags(key.ToArray(), WriteFlags.LowPriority);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Deferred_canonical_index_serves_before_and_after_flush()
    {
        (Block block, TxReceipt[] receipts) = PrepareBlock();
        _storage.InsertDeferred(block, receipts, _specProvider.GetSpec(block.Header));

        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));

        Hash256 txHash = block.Transactions[0].Hash!;
        Assert.That(_storage.FindBlockHash(txHash), Is.EqualTo(block.Hash), "pending index should serve before flush");

        _writer.Pump();

        Assert.That(_storage.FindBlockHash(txHash), Is.EqualTo(block.Hash), "database index should serve after flush");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Deferred_canonical_write_batches_insert_and_prune_with_low_priority()
    {
        _receiptConfig.TxLookupLimit = 1;
        Transaction oldTransaction = Build.A.Transaction.WithNonce(1).SignedAndResolved().TestObject;
        Block oldBlock = Build.A.Block.WithNumber(1).WithTransactions(oldTransaction).TestObject;
        Transaction newTransaction = Build.A.Transaction.WithNonce(2).SignedAndResolved().TestObject;
        Block newBlock = Build.A.Block.WithNumber(2).WithTransactions(newTransaction).TestObject;

        TestMemDb transactionDb = (TestMemDb)_receiptsDb.GetColumnDb(ReceiptsColumns.Transactions);
        transactionDb.Set(oldTransaction.Hash!.Bytes, oldBlock.Hash!.BytesToArray());
        _blockTree.FindBestSuggestedHeader().Returns(newBlock.Header);
        _blockTree.FindBlockHash(oldBlock.Number).Returns(oldBlock.Hash);
        _blockTree.FindBlockHash(newBlock.Number).Returns(newBlock.Hash);
        _blockStore.GetReceiptRecoveryBlock(oldBlock.Number, oldBlock.Hash).Returns(new ReceiptRecoveryBlock(oldBlock));

        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(newBlock));
        Assert.That(_storage.FindBlockHash(newTransaction.Hash!), Is.EqualTo(newBlock.Hash), "pending index before batch commit");

        _writer.Pump();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(transactionDb[oldTransaction.Hash.Bytes], Is.Null, "expired index removed");
            Assert.That(_storage.FindBlockHash(newTransaction.Hash!), Is.EqualTo(newBlock.Hash), "new index committed");
            Assert.That(_receiptsDb.WriteBatchCount, Is.EqualTo(1), "insert and prune share one RocksDB batch");
        }

        transactionDb.KeyWasWrittenWithFlags(oldTransaction.Hash.BytesToArray(), WriteFlags.LowPriority);
        transactionDb.KeyWasWrittenWithFlags(newTransaction.Hash!.BytesToArray(), WriteFlags.LowPriority);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Pending_index_scan_resolves_each_tx_to_its_own_block()
    {
        Block blockA = Build.A.Block
            .WithNumber(10)
            .WithTransactions(
                Build.A.Transaction.WithNonce(0).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject)
            .WithReceiptsRoot(TestItem.KeccakA).TestObject;
        Block blockB = Build.A.Block
            .WithNumber(11)
            .WithTransactions(Build.A.Transaction.WithNonce(0).SignedAndResolved(TestItem.PrivateKeyB).TestObject)
            .WithReceiptsRoot(TestItem.KeccakB).TestObject;

        foreach (Block b in (Block[])[blockA, blockB])
        {
            _blockTree.FindBlock(b.Hash!).Returns(b);
            _blockTree.FindBlockHash(b.Number).Returns(b.Hash);
        }
        _blockTree.FindBestSuggestedHeader().Returns(blockB.Header);

        TxReceipt[] receiptsA = [Build.A.Receipt.WithCalculatedBloom().TestObject, Build.A.Receipt.WithCalculatedBloom().TestObject];
        TxReceipt[] receiptsB = [Build.A.Receipt.WithCalculatedBloom().TestObject];

        _storage.InsertDeferred(blockA, receiptsA, _specProvider.GetSpec(blockA.Header));
        _storage.InsertDeferred(blockB, receiptsB, _specProvider.GetSpec(blockB.Header));
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(blockA));
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(blockB));

        // The lazy scan must resolve every tx of every pending block to its own block, not just the first.
        Assert.That(_storage.FindBlockHash(blockA.Transactions[0].Hash!), Is.EqualTo(blockA.Hash), "pending scan, block A tx 0");
        Assert.That(_storage.FindBlockHash(blockA.Transactions[1].Hash!), Is.EqualTo(blockA.Hash), "pending scan, block A tx 1");
        Assert.That(_storage.FindBlockHash(blockB.Transactions[0].Hash!), Is.EqualTo(blockB.Hash), "pending scan, block B tx 0");

        _writer.Pump();

        Assert.That(_storage.FindBlockHash(blockA.Transactions[1].Hash!), Is.EqualTo(blockA.Hash), "durable index, block A tx 1");
        Assert.That(_storage.FindBlockHash(blockB.Transactions[0].Hash!), Is.EqualTo(blockB.Hash), "durable index, block B tx 0");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Removed_receipts_are_not_resurrected_by_queued_writes()
    {
        (Block block, TxReceipt[] receipts) = PrepareBlock();
        _storage.InsertDeferred(block, receipts, _specProvider.GetSpec(block.Header));

        _storage.RemoveReceipts(block);
        _storage.ClearCache();

        Assert.That(_storage.Get(block), Is.Empty, "removed receipts must not be readable before flush");

        _writer.Pump();

        _storage.ClearCache();
        Assert.That(_storage.Get(block), Is.Empty, "a queued insert must not resurrect removed receipts");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Disabled_writer_falls_back_to_synchronous_insert()
    {
        await using DeferredBlockDataWriter disabled = DeferredWriteTestHelpers.DisabledWriter();
        PersistentReceiptStorage storage = CreateStorage(disabled);

        (Block block, TxReceipt[] receipts) = PrepareBlock();
        storage.InsertDeferred(block, receipts, _specProvider.GetSpec(block.Header));

        // Durable without any pump: a fresh storage over the same database can read it.
        PersistentReceiptStorage reopened = CreateStorage(null);
        reopened.Get(block).AssertEquivalentTo(receipts, nameof(TxReceipt.Error));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Writer_fault_falls_back_to_inline_execution()
    {
        await using DeferredBlockDataWriter writer = DeferredWriteTestHelpers.ManualWriter();
        int failures = 0;
        writer.Enqueue(() => { failures++; throw new InvalidOperationException("boom"); });

        writer.Pump();
        Assert.That(failures, Is.EqualTo(2), "failing item is retried once");

        bool ranInline = false;
        writer.Enqueue(() => ranInline = true);
        Assert.That(ranInline, Is.True, "after a fault, work executes inline on the producer");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Transient_write_fault_recovers_on_drain()
    {
        await using DeferredBlockDataWriter writer = DeferredWriteTestHelpers.ManualWriter();
        int attempts = 0;
        writer.Enqueue(() => { if (++attempts <= 2) throw new InvalidOperationException("transient"); });

        writer.Pump(); // both in-loop attempts fail, so the write is retained and the writer faults
        Assert.That(attempts, Is.EqualTo(2));

        // The drain retries the retained write inline; the disk has recovered, so it lands and persist proceeds.
        Assert.DoesNotThrow(() => writer.Drain());
        Assert.That(attempts, Is.EqualTo(3), "retained write retried once more at drain, then succeeded");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Persistent_write_fault_aborts_drain()
    {
        await using DeferredBlockDataWriter writer = DeferredWriteTestHelpers.ManualWriter();
        writer.Enqueue(() => throw new InvalidOperationException("disk down"));
        writer.Pump();

        Assert.Throws<InvalidOperationException>(() => writer.Drain(), "a still-failing write must abort state persistence");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public async Task Live_writer_drains_on_dispose()
    {
        await using DeferredBlockDataWriter writer = new(enabled: true, capacity: 8, LimboLogs.Instance);
        int ran = 0;
        for (int i = 0; i < 5; i++) writer.Enqueue(() => Interlocked.Increment(ref ran));

        await writer.DisposeAsync();
        Assert.That(ran, Is.EqualTo(5), "all queued work completes before dispose returns");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Barrier_flush_makes_receipts_durable()
    {
        (Block block, TxReceipt[] receipts) = PrepareBlock();
        _storage.InsertDeferred(block, receipts, _specProvider.GetSpec(block.Header));

        // Consumer is not running, so only the barrier (via the writer's drain) makes it durable.
        Assert.That(DurableReceipts(block), Is.Empty, "not durable until the barrier flushes");

        _barrier.FlushDeferred();

        DurableReceipts(block).AssertEquivalentTo(receipts, nameof(TxReceipt.Error));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Barrier_flush_makes_canonical_tx_index_durable()
    {
        (Block block, TxReceipt[] receipts) = PrepareBlock();
        _storage.InsertDeferred(block, receipts, _specProvider.GetSpec(block.Header));
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));

        Hash256 txHash = block.Transactions[0].Hash!;
        Assert.That(CreateStorage(null).FindBlockHash(txHash), Is.Null, "tx-index not durable until the barrier flushes");

        _barrier.FlushDeferred();

        Assert.That(CreateStorage(null).FindBlockHash(txHash), Is.EqualTo(block.Hash), "tx-index durable after the barrier flush");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Removed_receipts_stay_removed_when_flush_races_after_delete()
    {
        (Block block, TxReceipt[] receipts) = PrepareBlock();
        _storage.InsertDeferred(block, receipts, _specProvider.GetSpec(block.Header));

        // Delete happens while the write is still queued; the queued flush must then skip.
        _storage.RemoveReceipts(block);
        _writer.Pump();

        _storage.ClearCache();
        Assert.That(_storage.Get(block), Is.Empty);
        Assert.That(_storage.HasBlock(block.Number, block.Hash!), Is.False);
    }
}
