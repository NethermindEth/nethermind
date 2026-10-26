// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Encoding;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Receipts;


[TestFixture(true)]
[TestFixture(false)]
[Parallelizable(ParallelScope.All)]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class PersistentReceiptStorageTests(bool useCompactReceipts)
{
    private readonly TestSpecProvider _specProvider = new(Byzantium.Instance);
    private TestMemColumnsDb<ReceiptsColumns> _receiptsDb = null!;
    private ReceiptsRecovery _receiptsRecovery = null!;
    private IBlockTree _blockTree = null!;
    private IBlockStore _blockStore = null!;
    private ReceiptConfig _receiptConfig = null!;
    private PersistentReceiptStorage _storage = null!;
    private ReceiptArrayStorageDecoder _decoder = null!;
    private IStateHistoryCaptureStatus _captureStatus = null!;
    private ILogManager _logManager = null!;
    private InterfaceLogger _logger = null!;

    [SetUp]
    public void SetUp()
    {
        EthereumEcdsa ethereumEcdsa = new(_specProvider.ChainId);
        _receiptConfig = new ReceiptConfig();
        _receiptsRecovery = new(ethereumEcdsa, _specProvider);
        _receiptsDb = new TestMemColumnsDb<ReceiptsColumns>();
        _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Set(Keccak.Zero, Array.Empty<byte>());
        _blockTree = Substitute.For<IBlockTree>();
        _blockStore = Substitute.For<IBlockStore>();
        _logger = Substitute.For<InterfaceLogger>();
        _logger.IsWarn.Returns(true);
        ILogger logger = new(_logger);
        _logManager = Substitute.For<ILogManager>();
        _logManager.GetClassLogger<PersistentReceiptStorage>().Returns(logger);
        CreateStorage();
    }

    [TearDown]
    public void TearDown() => _receiptsDb.Dispose();

    private static IReceiptStorage[] NonPersistentImplementations() =>
    [
        NullReceiptStorage.Instance,
        new InMemoryReceiptStorage()
    ];

    [TestCaseSource(nameof(NonPersistentImplementations))]
    public void RemoveReceiptsRange_IsImplemented(IReceiptStorage storage) =>
        Assert.That(() => storage.RemoveReceiptsRange(1, 1000), Throws.Nothing,
            $"{storage.GetType().Name} inherits the throwing default, so a pruning node configured with it would fail every pass");

    [Test]
    public void InMemoryReceiptStorage_RemoveReceiptsByHash_TakesOnlyThatBlock()
    {
        InMemoryReceiptStorage storage = new();
        Dictionary<ulong, Block> blocks = [];

        for (ulong number = 1; number <= 3; number++)
        {
            Block block = Build.A.Block.WithNumber(number).WithTransactions(Build.A.Transaction.TestObject).TestObject;
            storage.Insert(block, [Build.A.Receipt.WithBlockHash(block.Hash).TestObject]);
            blocks[number] = block;
        }

        storage.RemoveReceipts(2, blocks[2].Hash!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage.HasBlock(1, blocks[1].Hash!), Is.True);
            Assert.That(storage.HasBlock(2, blocks[2].Hash!), Is.False);
            Assert.That(storage.HasBlock(3, blocks[3].Hash!), Is.True);
        }
    }

    [Test]
    public void InMemoryReceiptStorage_RemoveReceiptsRange_HoldsTheBounds()
    {
        InMemoryReceiptStorage storage = new();
        Dictionary<ulong, Block> blocks = [];

        for (ulong number = 1; number <= 5; number++)
        {
            Block block = Build.A.Block.WithNumber(number).WithTransactions(Build.A.Transaction.TestObject).TestObject;
            storage.Insert(block, [Build.A.Receipt.WithBlockHash(block.Hash).TestObject]);
            blocks[number] = block;
        }

        storage.RemoveReceiptsRange(2, 4);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storage.HasBlock(1, blocks[1].Hash!), Is.True);
            Assert.That(storage.HasBlock(2, blocks[2].Hash!), Is.False);
            Assert.That(storage.HasBlock(3, blocks[3].Hash!), Is.False);
            Assert.That(storage.HasBlock(4, blocks[4].Hash!), Is.True, "the upper bound is exclusive");
            Assert.That(storage.HasBlock(5, blocks[5].Hash!), Is.True);
        }
    }

    [Test]
    public void RemoveReceiptsRanges_TakesEveryRangeAndKeepsTheHeightsBetween()
    {
        Dictionary<ulong, Block> blocks = [];
        for (ulong number = 1; number <= 5; number++)
        {
            (Block block, _) = InsertBlock(Build.A.Block.WithNumber(number).WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject).TestObject);
            blocks[number] = block;
        }

        _storage.RemoveReceiptsRanges([(1, 3), (4, 5)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_storage.HasBlock(1, blocks[1].Hash!), Is.False);
            Assert.That(_storage.HasBlock(2, blocks[2].Hash!), Is.False);
            Assert.That(_storage.HasBlock(3, blocks[3].Hash!), Is.True, "the height between the ranges survives");
            Assert.That(_storage.HasBlock(4, blocks[4].Hash!), Is.False);
            Assert.That(_storage.HasBlock(5, blocks[5].Hash!), Is.True, "the upper bound is exclusive");
        }
    }

    [Test]
    public void RemoveReceiptsRanges_StopsServingACachedBlockInsideARange()
    {
        (Block block, _) = InsertBlock();
        Assert.That(_storage.Get(block), Is.Not.Empty, "priming the cache");

        _storage.RemoveReceiptsRanges([((ulong)block.Number, (ulong)block.Number + 1)]);

        Assert.That(_storage.Get(block), Is.Empty);
    }

    [Test]
    public void SweepTransactionIndex_DropsOnlyEntriesNamingReclaimedBlocks()
    {
        IDb txIndex = _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions);
        Hash256 stale = TestItem.KeccakA;
        Hash256 retained = TestItem.KeccakB;
        Hash256 legacy = TestItem.KeccakC;

        txIndex.Set(stale, Rlp.Encode(5UL).Bytes);
        txIndex.Set(retained, Rlp.Encode(50UL).Bytes);
        txIndex.Set(legacy, TestItem.KeccakD.BytesToArray());

        byte[]? cursor = _storage.SweepTransactionIndex(retainedFromBlock: 10, resumeFrom: null, maxEntries: 100, isHeightRetained: null, CancellationToken.None, out int removed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(txIndex.Get(stale), Is.Null);
            Assert.That(txIndex.Get(retained), Is.Not.Null);
            Assert.That(txIndex.Get(legacy), Is.Not.Null, "a hash whose header does not resolve is left alone rather than guessed at");
            Assert.That(removed, Is.EqualTo(1));
            Assert.That(cursor, Is.Null, "reaching the end reports no resume point");
        }
    }

    [Test]
    public void SweepTransactionIndex_KeepsAnEntryNamingARetainedHeightBelowTheBoundary()
    {
        IDb txIndex = _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions);
        Hash256 sliced = TestItem.KeccakA;
        Hash256 stale = TestItem.KeccakB;
        txIndex.Set(sliced, Rlp.Encode(5UL).Bytes);
        txIndex.Set(stale, Rlp.Encode(6UL).Bytes);

        _storage.SweepTransactionIndex(retainedFromBlock: 10, resumeFrom: null, maxEntries: 100, isHeightRetained: static height => height == 5, CancellationToken.None, out int removed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(txIndex.Get(sliced), Is.Not.Null, "a retained sliced height keeps its transactions findable by hash");
            Assert.That(txIndex.Get(stale), Is.Null);
            Assert.That(removed, Is.EqualTo(1));
        }
    }

    [Test]
    public void SweepTransactionIndex_DropsEntriesStoredAsBlockHash()
    {
        IDb txIndex = _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions);
        Block stale = Build.A.Block.WithNumber(5).TestObject;
        Block retained = Build.A.Block.WithNumber(50).TestObject;
        _blockTree.FindHeader(stale.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(stale.Header);
        _blockTree.FindHeader(retained.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(retained.Header);

        txIndex.Set(TestItem.KeccakA, stale.Hash!.BytesToArray());
        txIndex.Set(TestItem.KeccakB, retained.Hash!.BytesToArray());

        _storage.SweepTransactionIndex(retainedFromBlock: 10, resumeFrom: null, maxEntries: 100, isHeightRetained: null, CancellationToken.None, out int removed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(txIndex.Get(TestItem.KeccakA), Is.Null,
                "CompactTxIndex=false stores the block hash, and this one names a block below the boundary - the only mechanism that reclaims it");
            Assert.That(txIndex.Get(TestItem.KeccakB), Is.Not.Null);
            Assert.That(removed, Is.EqualTo(1));
        }
    }

    // Backwards, this either walks the whole index every pass finding nothing or lets it grow without bound.
    [TestCase(1_000_000ul, 2_500_000ul, true, TestName = "Sweeps_once_pruning_has_passed_the_lookup_horizon")]
    [TestCase(2_000_000ul, 500_000ul, false, TestName = "Leaves_the_index_alone_below_the_horizon")]
    [TestCase(4_000_000ul, 2_500_000ul, true, TestName = "Sweeps_when_head_is_short_of_the_limit_and_nothing_else_ever_will")]
    [TestCase(0ul, 2_500_000ul, false, TestName = "Leaves_the_index_alone_when_the_limit_is_the_index_forever_sentinel")]
    [TestCase(ulong.MaxValue, 2_500_000ul, false, TestName = "Leaves_the_index_alone_when_the_limit_is_the_never_index_sentinel")]
    public void SweepTransactionIndex_RunsOnlyOncePastTheLookupHorizon(ulong txLookupLimit, ulong retainedFromBlock, bool expectSwept)
    {
        _receiptConfig.TxLookupLimit = txLookupLimit;
        _blockTree.Head.Returns(Build.A.Block.WithNumber(3_000_000).TestObject);
        CreateStorage();

        IDb txIndex = _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions);
        txIndex.Set(TestItem.KeccakA, Rlp.Encode(100UL).Bytes);

        _storage.SweepTransactionIndex(retainedFromBlock, resumeFrom: null, maxEntries: 100, isHeightRetained: null, CancellationToken.None, out int removed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(removed, Is.EqualTo(expectSwept ? 1 : 0));
            Assert.That(txIndex.Get(TestItem.KeccakA), expectSwept ? Is.Null : Is.Not.Null,
                expectSwept
                    ? "above the horizon the per-block path can no longer reach these, so the sweep is the only mechanism"
                    : "everywhere else master retains the index, and destroying entries an operator asked to keep cannot be undone");
        }
    }

    [Test]
    public void SweepTransactionIndex_CancelledWalk_StillCoversItsMinimumSlice()
    {
        IDb txIndex = _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions);
        foreach (Hash256 key in new[] { TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC, TestItem.KeccakD })
        {
            txIndex.Set(key, Rlp.Encode(5UL).Bytes);
        }

        using CancellationTokenSource cts = new();
        cts.Cancel();

        byte[]? cursor = _storage.SweepTransactionIndex(retainedFromBlock: 10, resumeFrom: null, maxEntries: 100, isHeightRetained: null, cts.Token, out int removed);

        using (Assert.EnterMultipleScope())
        {
            // Fewer entries than the minimum slice, so a spent budget must not stop the walk before it does anything.
            Assert.That(removed, Is.EqualTo(4));
            Assert.That(cursor, Is.Null, "the column ended before the slice did");
        }
    }

    [Test]
    public void SweepTransactionIndex_CancelledPastItsMinimumSlice_ReportsWhereToResume()
    {
        IDb txIndex = _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions);
        const int entries = 5000;
        for (int i = 0; i < entries; i++)
        {
            txIndex.Set(Keccak.Compute(i.ToBigEndianByteArray()), Rlp.Encode(5UL).Bytes);
        }

        using CancellationTokenSource cts = new();
        cts.Cancel();

        byte[]? cursor = _storage.SweepTransactionIndex(retainedFromBlock: 10, resumeFrom: null, maxEntries: entries * 2, isHeightRetained: null, cts.Token, out int removed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cursor, Is.Not.Null, "past the minimum slice the token has to be honoured, or the budget means nothing");
            Assert.That(removed, Is.LessThan(entries), "and it has to stop short of the column");
            Assert.That(removed, Is.GreaterThan(0));
        }
    }

    [Test]
    public void SweepTransactionIndex_HonoursItsBudgetAndResumes()
    {
        IDb txIndex = _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions);
        Hash256[] keys = [TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC, TestItem.KeccakD];
        foreach (Hash256 key in keys)
        {
            txIndex.Set(key, Rlp.Encode(5UL).Bytes);
        }

        byte[]? cursor = _storage.SweepTransactionIndex(retainedFromBlock: 10, resumeFrom: null, maxEntries: 2, isHeightRetained: null, CancellationToken.None, out int firstRemoved);
        Assert.That(cursor, Is.Not.Null, "stopping on budget has to report where to pick up, or the tail is never reached");

        int total = firstRemoved;
        while (cursor is not null)
        {
            cursor = _storage.SweepTransactionIndex(retainedFromBlock: 10, resumeFrom: cursor, maxEntries: 2, isHeightRetained: null, CancellationToken.None, out int removed);
            total += removed;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(total, Is.EqualTo(keys.Length), "resuming from the reported key must eventually cover every entry");
            foreach (Hash256 key in keys)
            {
                Assert.That(txIndex.Get(key), Is.Null);
            }
        }
    }

    private void CreateStorage(bool captureHealthy = true, bool configuredDecoder = false)
    {
        _decoder = new ReceiptArrayStorageDecoder(useCompactReceipts);
        IStateHistoryCaptureStatus captureStatus = Substitute.For<IStateHistoryCaptureStatus>();
        captureStatus.CaptureHealthy.Returns(captureHealthy);
        _captureStatus = captureStatus;
        _storage = new PersistentReceiptStorage(
            _receiptsDb,
            _specProvider,
            _receiptsRecovery,
            _blockTree,
            _blockStore,
            _receiptConfig,
            configuredDecoder ? null : _decoder,
            logManager: _logManager,
            historyCaptureStatus: captureStatus
        )
        { MigratedBlockNumber = 0 };
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Returns_null_for_missing_tx()
    {
        Hash256? blockHash = _storage.FindBlockHash(Keccak.Zero);
        Assert.That(blockHash, Is.Null);
    }

    /// <summary>
    /// The receipt backfill keeps its lowest-inserted-block pointer in the legacy tx-hash keyed column. A lookup of
    /// that key is not a transaction and must not decode the pointer as a receipt. 24,600,000 is the mainnet ancient
    /// receipts barrier: RLP 0x84 0x01 0x77..., which used to fail as "expected a sequence prefix ... got 119".
    /// </summary>
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlockHash_returns_null_for_the_lowest_inserted_receipt_pointer_key()
    {
        _receiptsDb.GetColumnDb(ReceiptsColumns.Default)
            .Set(PersistentReceiptStorage.LowestInsertedReceiptBlockKey, Rlp.Encode(24_600_000UL).Bytes);

        Assert.That(_storage.FindBlockHash(Keccak.Zero), Is.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlockHash_returns_null_for_the_migration_progress_key()
    {
        _storage.MigratedBlockNumber = 24_600_000;

        Assert.That(_storage.FindBlockHash(Keccak.Compute(nameof(PersistentReceiptStorage.MigratedBlockNumber))), Is.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void FindBlockHash_still_serves_a_receipt_stored_in_the_legacy_format()
    {
        TxReceipt receipt = Build.A.Receipt.WithBlockHash(TestItem.KeccakB).WithTransactionHash(TestItem.KeccakA).TestObject;
        _receiptsDb.GetColumnDb(ReceiptsColumns.Default)
            .Set(TestItem.KeccakA, new ReceiptStorageDecoder().Encode(receipt, RlpBehaviors.Storage).Bytes);

        Assert.That(_storage.FindBlockHash(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakB));
    }

    [Test]
    public void FindBlockHash_buffer_read_resolves_the_current_canonical_number([Values(0UL, 1UL, ulong.MaxValue)] ulong number)
    {
        IndexReadDb index = new();
        index.Set(TestItem.KeccakA, Rlp.Encode(number).Bytes);
        PersistentReceiptStorage storage = CreateStorageWithIndex(index);
        _blockTree.FindBlockHash(number).Returns(TestItem.KeccakB);
        Assert.That(storage.FindBlockHash(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakB));
        _blockTree.FindBlockHash(number).Returns(TestItem.KeccakC);
        Assert.That(storage.FindBlockHash(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakC));
        _blockTree.FindBlockHash(number).Returns((Hash256?)null);
        Assert.That(storage.FindBlockHash(TestItem.KeccakA), Is.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(index.BufferReads, Is.EqualTo(3));
            Assert.That(index.ArrayReads, Is.Zero);
        }
    }

    [Test]
    public void FindBlockHash_buffer_read_preserves_a_legacy_hash()
    {
        IndexReadDb index = new();
        index.Set(TestItem.KeccakA, TestItem.KeccakB.BytesToArray());
        PersistentReceiptStorage storage = CreateStorageWithIndex(index);
        Assert.That(storage.FindBlockHash(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakB));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(index.BufferReads, Is.EqualTo(1));
            Assert.That(index.ArrayReads, Is.Zero);
        }
    }

    [Test]
    public void FindBlockHash_missing_index_preserves_the_obsolete_receipt_fallback([Values] bool legacyReceipt)
    {
        IndexReadDb index = new();
        if (legacyReceipt)
        {
            TxReceipt receipt = Build.A.Receipt.WithBlockHash(TestItem.KeccakB).WithTransactionHash(TestItem.KeccakA).TestObject;
            _receiptsDb.GetColumnDb(ReceiptsColumns.Default)
                .Set(TestItem.KeccakA, new ReceiptStorageDecoder().Encode(receipt, RlpBehaviors.Storage).Bytes);
        }
        PersistentReceiptStorage storage = CreateStorageWithIndex(index);
        Assert.That(storage.FindBlockHash(TestItem.KeccakA), Is.EqualTo(legacyReceipt ? TestItem.KeccakB : null));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(index.BufferReads, Is.EqualTo(1));
            Assert.That(index.ArrayReads, Is.EqualTo(1));
        }
    }

    private static IEnumerable<byte[]> MalformedIndexValues() =>
    [
        [],
        [0],
        [0x89, 1, 2, 3, 4, 5, 6, 7, 8, 9],
        Enumerable.Repeat((byte)0xc0, Hash256.Size + 1).ToArray()
    ];

    [Test]
    public void FindBlockHash_preserves_malformed_index_exceptions([ValueSource(nameof(MalformedIndexValues))] byte[] value)
    {
        IndexReadDb index = new();
        index.Set(TestItem.KeccakA, value);
        PersistentReceiptStorage storage = CreateStorageWithIndex(index);
        Exception? original = Assert.Catch(() => new RlpReader(value).DecodeULong());
        Assert.That(original, Is.Not.Null);
        Assert.That(() => storage.FindBlockHash(TestItem.KeccakA), Throws.TypeOf(original!.GetType()));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(index.BufferReads, Is.EqualTo(1));
            Assert.That(index.ArrayReads, Is.EqualTo(value.Length == 0 || value.Length > Hash256.Size ? 1 : 0));
        }
    }

    [Test]
    public void FindBlockHash_preserves_oversized_values_with_a_valid_rlp_prefix()
    {
        IndexReadDb index = new();
        byte[] value = new byte[Hash256.Size + 1];
        value[0] = 1;
        index.Set(TestItem.KeccakA, value);
        PersistentReceiptStorage storage = CreateStorageWithIndex(index);
        _blockTree.FindBlockHash(1UL).Returns(TestItem.KeccakB);
        Assert.That(storage.FindBlockHash(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakB));
        Assert.That(index.ArrayReads, Is.EqualTo(1));
    }

    [Test]
    public void FindBlockHash_falls_back_when_the_buffer_read_is_rejected([Values] bool unsupported)
    {
        IndexReadDb index = new()
        {
            BufferFailure = unsupported ? new NotSupportedException("caller buffer") : new ArgumentException("capacity")
        };
        index.Set(TestItem.KeccakA, Rlp.Encode(1UL).Bytes);
        PersistentReceiptStorage storage = CreateStorageWithIndex(index);
        _blockTree.FindBlockHash(1UL).Returns(TestItem.KeccakB);
        Assert.That(storage.FindBlockHash(TestItem.KeccakA), Is.EqualTo(TestItem.KeccakB));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(index.BufferReads, Is.EqualTo(1));
            Assert.That(index.ArrayReads, Is.EqualTo(1));
        }
    }

    [Test]
    public void FindBlockHash_propagates_io_errors_without_retrying([Values] bool failFallback)
    {
        IOException failure = new("index read failure");
        IndexReadDb index = new()
        {
            BufferFailure = failFallback ? new ArgumentException("capacity") : failure,
            ArrayFailure = failFallback ? failure : null
        };
        PersistentReceiptStorage storage = CreateStorageWithIndex(index);
        Exception? thrown = Assert.Catch(() => storage.FindBlockHash(TestItem.KeccakA));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.SameAs(failure));
            Assert.That(index.ArrayReads, Is.EqualTo(failFallback ? 1 : 0));
        }
    }

    [Test]
    public async Task FindBlockHash_pending_index_precedes_both_db_read_paths([Values] bool compactIndex)
    {
        _receiptConfig.CompactTxIndex = compactIndex;
        IndexReadDb index = new() { BufferFailure = new IOException("must not read the index") };
        StatePersistenceBarrier barrier = new();
        await using DeferredBlockDataWriter writer = DeferredWriteTestHelpers.ManualWriter(barrier);
        PersistentReceiptStorage storage = CreateStorageWithIndex(index, writer, barrier);
        (Block block, TxReceipt[] _) = PrepareBlock();
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));
        Hash256 hash = block.Transactions[0].Hash!;
        Assert.That(storage.FindBlockHash(hash), Is.EqualTo(block.Hash));
        _blockTree.FindBlockHash(block.Number).Returns(TestItem.KeccakC);
        Assert.That(storage.FindBlockHash(hash), Is.EqualTo(compactIndex ? TestItem.KeccakC : block.Hash));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(index.BufferReads, Is.Zero);
            Assert.That(index.ArrayReads, Is.Zero);
        }
    }

    private PersistentReceiptStorage CreateStorageWithIndex(IDb index, IDeferredBlockDataWriter? writer = null, IStatePersistenceBarrier? barrier = null)
    {
        IColumnsDb<ReceiptsColumns> columns = Substitute.For<IColumnsDb<ReceiptsColumns>>();
        columns.GetColumnDb(Arg.Any<ReceiptsColumns>()).Returns(call =>
            call.Arg<ReceiptsColumns>() == ReceiptsColumns.Transactions ? index : _receiptsDb.GetColumnDb(call.Arg<ReceiptsColumns>()));
        columns.StartWriteBatch().Returns(_ => _receiptsDb.StartWriteBatch());
        return new PersistentReceiptStorage(columns, _specProvider, _receiptsRecovery, _blockTree, _blockStore,
            _receiptConfig, _decoder, writer, barrier, _logManager);
    }

    private sealed class IndexReadDb : TestMemDb, IReadOnlyKeyValueStore
    {
        public int BufferReads { get; private set; }
        public int ArrayReads { get; private set; }
        public Exception? BufferFailure { get; init; }
        public Exception? ArrayFailure { get; init; }

        public override byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            ArrayReads++;
            if (ArrayFailure is not null) throw ArrayFailure;
            return base.Get(key, flags);
        }

        int IReadOnlyKeyValueStore.Get(scoped ReadOnlySpan<byte> key, Span<byte> output, ReadFlags flags)
        {
            BufferReads++;
            if (BufferFailure is not null) throw BufferFailure;
            byte[]? bytes = base.Get(key, flags);
            if (bytes is null) return 0;
            bytes.AsSpan().CopyTo(output);
            return bytes.Length;
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ReceiptsIterator_does_not_throw_on_empty_span()
    {
        _storage.TryGetReceiptsIterator(1, Keccak.Zero, out ReceiptsIterator iterator);
        Assert.That(iterator.TryGetNext(out _), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void ReceiptsIterator_does_not_throw_on_null()
    {
        _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Set(Keccak.Zero, null!);
        _storage.TryGetReceiptsIterator(1, Keccak.Zero, out ReceiptsIterator iterator);
        Assert.That(iterator.TryGetNext(out _), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Get_returns_empty_on_empty_span() =>
        Assert.That(_storage.Get(Keccak.Zero), Is.EqualTo(Array.Empty<TxReceipt>()));

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Migration_read_preserves_legacy_missing_receipts()
    {
        (Block block, TxReceipt[] receipts) = PrepareBlock();
        TxReceipt[] legacyReceipts = [receipts[0], null!];
        using ArrayPoolSpan<byte> encoded = _decoder.EncodeToArrayPoolSpan(legacyReceipts, RlpBehaviors.Storage);
        Hash256 blockHash = block.Hash ?? throw new AssertionException("Test block hash is missing.");
        byte[] encodedBytes = ((ReadOnlySpan<byte>)encoded).ToArray();
        Span<byte> encodedSpan = encodedBytes;
        Assert.That(_decoder.DecodeAllowingMissing(in encodedSpan), Has.Length.EqualTo(2));

        _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks)[blockHash.Bytes] = encodedBytes;
        _storage.ClearCache();
        Assert.That(_storage.Get(blockHash), Has.Length.EqualTo(1));

        TxReceipt?[] migrationReceipts = ((IReceiptMigrationStore)_storage)
            .GetForMigration(block.Number, blockHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(migrationReceipts, Has.Length.EqualTo(2));
            Assert.That(migrationReceipts[0], Is.Not.Null);
            Assert.That(migrationReceipts[1], Is.Null);
        }
    }

    [TestCase(true, 1)]
    [TestCase(false, 0)]
    [MaxTime(Timeout.MaxTestTime)]
    public void Get_logs_legacy_receipt_count_mismatch_only_on_recovering_reads(bool recover, int expectedWarnings)
    {
        Block block = Build.A.Block
            .WithNumber(1)
            .WithTransactions(
                Build.A.Transaction.SignedAndResolved().TestObject,
                Build.A.Transaction.SignedAndResolved().TestObject,
                Build.A.Transaction.SignedAndResolved().TestObject)
            .WithReceiptsRoot(TestItem.KeccakA)
            .TestObject;
        PrepareBlock(block);
        TxReceipt[] legacyReceipts =
        [
            Build.A.Receipt.WithCalculatedBloom().TestObject,
            null!,
            Build.A.Receipt.WithCalculatedBloom().TestObject
        ];
        byte[] encoded = _decoder.EncodeAsBytes(legacyReceipts, RlpBehaviors.Storage);
        _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks)[block.Hash!.Bytes] = encoded;

        TxReceipt[] receipts = _storage.Get(block, recover);

        Assert.That(receipts, Has.Length.EqualTo(1));
        _logger.Received(expectedWarnings).Warn(Arg.Is<string>(message =>
            message.Contains(block.ToString(Block.Format.FullHashAndNumber)) &&
            message.Contains("decoded 1 for 3 transactions")));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Adds_and_retrieves_receipts_for_block()
    {
        (Block? block, TxReceipt[]? receipts) = InsertBlock();

        _storage.ClearCache();
        _storage.Get(block).AssertEquivalentTo(receipts, nameof(TxReceipt.Error));
        // second should be from cache
        _storage.Get(block).AssertEquivalentTo(receipts, nameof(TxReceipt.Error));
    }

    [Test]
    public void Get_serves_zero_length_blooms_after_eip7668([Values] bool eip7668)
    {
        _specProvider.NextForkSpec = eip7668 ? new OverridableReleaseSpec(Bogota.Instance) { IsEip7668Enabled = true } : Bogota.Instance;
        // The storage reads whether the chain ever schedules EIP-7668 on construction.
        CreateStorage();
        (Block block, _) = PrepareBlock();
        // A receipt synced without a bloom would compute one lazily on read.
        TxReceipt receipt = Build.A.Receipt.WithLogs(new LogEntry(TestItem.AddressA, [], [TestItem.KeccakA])).TestObject;
        receipt.Bloom = null;

        _storage.Insert(block, [receipt]);
        _storage.ClearCache();

        Assert.That(_storage.Get(block).Select(static r => r.Bloom.IsZeroLength), Is.All.EqualTo(eip7668));
    }

    [Test]
    public void TryGetReceipt_matches_the_receipt_Get_recovers([Values] bool eip7668, [Values] bool eip8116, [Values] bool fromRlp)
    {
        _specProvider.NextForkSpec = new OverridableReleaseSpec(Bogota.Instance) { IsEip7668Enabled = eip7668, IsEip8116Enabled = eip8116 };
        CreateStorage();
        LogEntry log = new(TestItem.AddressA, [1, 2], [TestItem.KeccakA]);
        Block block = Build.A.Block.WithNumber(1).WithReceiptsRoot(TestItem.KeccakA).WithTransactions(
            Build.A.Transaction.WithNonce(0).SignedAndResolved().TestObject,
            Build.A.Transaction.WithNonce(1).WithTo(null).WithCode([0x00]).SignedAndResolved().TestObject,
            Build.A.Transaction.WithNonce(2).SignedAndResolved().TestObject,
            Build.A.Transaction.WithNonce(3).SignedAndResolved().TestObject).TestObject;
        PrepareBlock(block);
        TxReceipt[] receipts = WithTransactionHashes(block,
            Build.A.Receipt.WithLogs(log, log).WithGasUsedTotal(30_000).TestObject,
            Build.A.Receipt.WithStatusCode(StatusCode.Failure).WithGasUsedTotal(80_000).TestObject,
            Build.A.Receipt.WithLogs(log).WithGasUsedTotal(110_000).TestObject,
            Build.A.Receipt.WithGasUsedTotal(131_000).TestObject);
        _storage.Insert(block, receipts);

        ClearSendersAndCache();
        TxReceipt[] expected = _storage.Get(block);
        Address?[] senders = block.Transactions.Select(static tx => tx.SenderAddress).ToArray();
        for (int i = 0; i < receipts.Length; i++)
        {
            ClearSendersAndCache();
            bool served = _storage.TryGetReceipt(RecoveryBlock(block, fromRlp), block.Transactions[i].Hash!, out TxReceipt? receipt, out int txIndex, out int logIndexStart);

            Assert.That(served, Is.EqualTo(useCompactReceipts));
            if (!served) continue;
            receipt.AssertEquivalentTo(expected[i]);
            Assert.That(txIndex, Is.EqualTo(i));
            Assert.That(logIndexStart, Is.EqualTo(expected.GetBlockLogFirstIndex(i)));
            if (!fromRlp) Assert.That(block.Transactions[i].SenderAddress, Is.EqualTo(senders[i]));
        }

        ClearSendersAndCache();
        Assert.That(_storage.TryGetReceipt(RecoveryBlock(block, fromRlp), TestItem.KeccakH, out _, out _, out _), Is.False, "a transaction outside the block");
        Assert.That(_storage.TryGetReceipt(RecoveryBlock(block, fromRlp), block.Transactions[0].Hash!, out _, out _, out _), Is.False, "a repeat lookup in one block is served by Get, which caches its receipts");

        TxReceipt[] cached = _storage.Get(block);
        Assert.That(_storage.TryGetReceipt(RecoveryBlock(block, fromRlp), block.Transactions[2].Hash!, out TxReceipt? fromCache, out int cachedIndex, out int cachedLogIndexStart), Is.True);
        Assert.That(fromCache, Is.SameAs(cached[2]));
        Assert.That(cachedIndex, Is.EqualTo(2));
        Assert.That(cachedLogIndexStart, Is.EqualTo(cached.GetBlockLogFirstIndex(2)));

        void ClearSendersAndCache()
        {
            foreach (Transaction tx in block.Transactions) tx.SenderAddress = null;
            _storage.ClearCache();
        }
    }

    [Test]
    public void TryGetReceipt_falls_back_to_Get_on_a_repeat_lookup_in_interleaved_blocks()
    {
        CreateStorage();
        Block[] blocks = [InsertTwoTransactionBlock(1), InsertTwoTransactionBlock(2)];
        _storage.ClearCache();

        foreach (Block block in blocks)
        {
            Assert.That(_storage.TryGetReceipt(new ReceiptRecoveryBlock(block), block.Transactions[0].Hash!, out _, out _, out _), Is.EqualTo(useCompactReceipts));
        }

        foreach (Block block in blocks)
        {
            Assert.That(_storage.TryGetReceipt(new ReceiptRecoveryBlock(block), block.Transactions[1].Hash!, out _, out _, out _), Is.False, $"a repeat lookup in block {block.Number} is served by Get");
        }

        Block InsertTwoTransactionBlock(ulong number)
        {
            Block block = Build.A.Block.WithNumber(number).WithReceiptsRoot(TestItem.KeccakA).WithTransactions(
                Build.A.Transaction.WithNonce(0).SignedAndResolved().TestObject,
                Build.A.Transaction.WithNonce(1).SignedAndResolved().TestObject).TestObject;
            PrepareBlock(block);
            _storage.Insert(block, WithTransactionHashes(block, Build.A.Receipt.WithGasUsedTotal(21_000).TestObject, Build.A.Receipt.WithGasUsedTotal(42_000).TestObject));
            return block;
        }
    }

    [Test]
    public void TryGetReceipt_falls_back_to_Get_when_stored_receipts_do_not_match_the_transactions([Values] bool emptyItem)
    {
        Block block = Build.A.Block.WithNumber(1).WithReceiptsRoot(TestItem.KeccakA).WithTransactions(
            Build.A.Transaction.WithNonce(0).SignedAndResolved().TestObject,
            Build.A.Transaction.WithNonce(1).SignedAndResolved().TestObject,
            Build.A.Transaction.WithNonce(2).SignedAndResolved().TestObject).TestObject;
        PrepareBlock(block);
        TxReceipt receipt = Build.A.Receipt.WithTransactionHash(block.Transactions[0].Hash).WithGasUsedTotal(21_000).TestObject;
        TxReceipt[] stored = emptyItem ? [receipt, null!, receipt] : [receipt, receipt];
        _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks)[block.Hash!.Bytes] = _decoder.EncodeAsBytes(stored, RlpBehaviors.Storage);

        Assert.That(_storage.TryGetReceipt(new ReceiptRecoveryBlock(block), block.Transactions[0].Hash!, out _, out _, out _), Is.False);
    }

    [Test]
    public void TryGetReceipt_falls_back_to_Get_when_receipts_store_no_transaction_hashes()
    {
        Block block = Build.A.Block.WithNumber(1).WithReceiptsRoot(TestItem.KeccakA)
            .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject).TestObject;
        PrepareBlock(block);
        TxReceipt[] stored = WithTransactionHashes(block, Build.A.Receipt.WithGasUsedTotal(21_000).TestObject);
        _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks)[block.Hash!.Bytes] = ReceiptArrayStorageDecoder.WithoutTxHashes.EncodeAsBytes(stored, RlpBehaviors.Storage);

        Assert.That(_storage.TryGetReceipt(new ReceiptRecoveryBlock(block), block.Transactions[0].Hash!, out _, out _, out _), Is.False);
    }

    [Test]
    public void TryGetReceipt_skips_a_frame_receipt_before_the_requested_one()
    {
        Transaction frameTx = new()
        {
            Type = TxType.FrameTx,
            SenderAddress = TestItem.AddressA,
            Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, default, default)],
            FrameSignatures = [],
        };
        frameTx.Hash = frameTx.CalculateHash();
        Block block = Build.A.Block.WithNumber(1).WithReceiptsRoot(TestItem.KeccakA)
            .WithTransactions(frameTx, Build.A.Transaction.WithNonce(1).SignedAndResolved().TestObject).TestObject;
        PrepareBlock(block);
        LogEntry log = new(TestItem.AddressB, [1], [TestItem.KeccakA]);
        TxReceipt frameReceipt = Build.A.Receipt.WithLogs(log, log).WithGasUsedTotal(50_000).TestObject;
        frameReceipt.TxType = TxType.FrameTx;
        frameReceipt.Payer = TestItem.AddressA;
        frameReceipt.FrameReceipts = [new TxFrameReceipt(TxFrameReceipt.StatusSuccess, 50_000, 0, [log, log])];
        _storage.Insert(block, WithTransactionHashes(block, frameReceipt, Build.A.Receipt.WithLogs(log).WithGasUsedTotal(71_000).TestObject));
        _storage.ClearCache();

        bool served = _storage.TryGetReceipt(new ReceiptRecoveryBlock(block), block.Transactions[1].Hash!, out TxReceipt? receipt, out _, out int logIndexStart);

        Assert.That(served, Is.EqualTo(useCompactReceipts));
        if (!served) return;
        TxReceipt[] expected = _storage.Get(block);
        receipt.AssertEquivalentTo(expected[1]);
        Assert.That(logIndexStart, Is.EqualTo(expected.GetBlockLogFirstIndex(1)));
    }

    private static TxReceipt[] WithTransactionHashes(Block block, params TxReceipt[] receipts)
    {
        for (int i = 0; i < receipts.Length; i++) receipts[i].TxHash = block.Transactions[i].Hash;
        return receipts;
    }

    private static ReceiptRecoveryBlock RecoveryBlock(Block block, bool fromRlp) =>
        fromRlp
            ? new BlockDecoder().DecodeToReceiptRecoveryBlock(null, new BlockDecoder().EncodeAsBytes(block), RlpBehaviors.None)!.Value
            : new ReceiptRecoveryBlock(block);

    [Test]
    public void Adds_should_prefix_key_with_blockNumber()
    {
        (Block block, _) = InsertBlock();

        Span<byte> blockNumPrefixed = stackalloc byte[40];
        block.Number.ToBigEndianByteArray().CopyTo(blockNumPrefixed); // TODO: We don't need to create an array here...
        block.Hash!.Bytes.CopyTo(blockNumPrefixed[8..]);

        Assert.That(_receiptsDb.GetColumnDb(ReceiptsColumns.Blocks)[blockNumPrefixed], Is.Not.Null);
    }

    [Test]
    public void Adds_should_forward_write_flags()
    {
        (Block block, _) = InsertBlock(writeFlags: WriteFlags.DisableWAL);

        Span<byte> blockNumPrefixed = stackalloc byte[40];
        block.Number.ToBigEndianByteArray().CopyTo(blockNumPrefixed); // TODO: We don't need to create an array here...
        block.Hash!.Bytes.CopyTo(blockNumPrefixed[8..]);

        TestMemDb blockDb = (TestMemDb)_receiptsDb.GetColumnDb(ReceiptsColumns.Blocks);

        blockDb.KeyWasWrittenWithFlags(blockNumPrefixed.ToArray(), WriteFlags.DisableWAL);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Get_receipts_for_block_without_recovering_sender()
    {
        (Block? block, TxReceipt[]? receipts) = InsertBlock();
        foreach (Transaction tx in block.Transactions)
        {
            tx.SenderAddress = null;
        }

        _storage.ClearCache();
        _storage.Get(block, recoverSender: false).AssertEquivalentTo(receipts, nameof(TxReceipt.Error));

        foreach (Transaction tx in block.Transactions)
        {
            Assert.That(tx.SenderAddress, Is.Null);
        }
    }

    // An insert from sync or era import, and a read by block hash (a peer's GetReceipts), recover without senders. On a
    // block decoded from the database, whose transactions carry none, a creation's contract address then stays unset,
    // so those receipts must not serve a later read that asks for full recovery (eth_getBlockReceipts).
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Does_not_cache_receipts_recovered_without_senders([Values] bool readByBlockHash)
    {
        Transaction creation = Build.A.Transaction.WithTo(null).WithCode([0x00]).SignedAndResolved().TestObject;
        Address deployed = ContractAddress.From(creation.SenderAddress, creation.Nonce);
        (Block block, TxReceipt[] receipts) = PrepareBlock(Build.A.Block.WithNumber(1).WithTransactions(creation).WithReceiptsRoot(TestItem.KeccakA).TestObject);

        creation.SenderAddress = null;
        _storage.Insert(block, receipts);
        if (readByBlockHash) _storage.Get(block.Hash!);

        Assert.That(_storage.Get(block)[0].ContractAddress, Is.EqualTo(deployed));
    }

    [Test]
    public void Adds_should_attempt_hash_key_first_if_inserted_with_hashkey()
    {
        (Block block, TxReceipt[] receipts) = PrepareBlock();

        using ArrayPoolSpan<byte> encodedReceipts = _decoder.EncodeToArrayPoolSpan(receipts, RlpBehaviors.Storage);
        _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks)[block.Hash!.Bytes] = ((ReadOnlySpan<byte>)encodedReceipts).ToArray();

        CreateStorage();
        _storage.Get(block);

        Span<byte> blockNumPrefixed = stackalloc byte[40];
        block.Number.ToBigEndianByteArray().CopyTo(blockNumPrefixed); // TODO: We don't need to create an array here...
        block.Hash!.Bytes.CopyTo(blockNumPrefixed[8..]);

        TestMemDb blocksDb = (TestMemDb)_receiptsDb.GetColumnDb(ReceiptsColumns.Blocks);
        blocksDb.KeyWasRead(blockNumPrefixed.ToArray(), times: 0);
        blocksDb.KeyWasRead(block.Hash.BytesToArray(), times: 1);
    }

    [Test]
    public void Should_be_able_to_get_block_with_hash_address()
    {
        (Block block, TxReceipt[] receipts) = PrepareBlock();

        Span<byte> blockNumPrefixed = stackalloc byte[40];
        block.Number.ToBigEndianByteArray().CopyTo(blockNumPrefixed); // TODO: We don't need to create an array here...
        block.Hash!.Bytes.CopyTo(blockNumPrefixed[8..]);

        using ArrayPoolSpan<byte> encodedReceipts = _decoder.EncodeToArrayPoolSpan(receipts, RlpBehaviors.Storage);
        _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks)[block.Hash.Bytes] = ((ReadOnlySpan<byte>)encodedReceipts).ToArray();

        Assert.That(_storage.Get(block).Length, Is.EqualTo(receipts.Length));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Should_not_cache_empty_non_processed_blocks()
    {
        Block block = Build.A.Block
            .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject)
            .WithReceiptsRoot(TestItem.KeccakA)
            .TestObject;

        TxReceipt[] emptyReceipts = [];
        Assert.That(_storage.Get(block), Is.EqualTo(emptyReceipts));
        // can be from cache:
        Assert.That(_storage.Get(block), Is.EqualTo(emptyReceipts));
        (_, TxReceipt[] receipts) = InsertBlock(block);
        // before should not be cached
        Assert.That(_storage.Get(block), Is.EqualTo(receipts));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Adds_and_retrieves_receipts_for_block_with_iterator_from_cache_after_insert()
    {
        (Block? block, TxReceipt[]? receipts) = InsertBlock();

        Assert.That(_storage.TryGetReceiptsIterator(0, block.Hash!, out ReceiptsIterator iterator), Is.True);
        Assert.That(iterator.TryGetNext(out TxReceiptStructRef receiptStructRef), Is.True);
        Assert.That(receiptStructRef.LogsRlp.ToArray(), Is.Empty);
        receiptStructRef.Logs.AssertEquivalentTo(receipts.First().Logs);
        Assert.That(iterator.TryGetNext(out _), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Adds_and_retrieves_receipts_for_block_with_iterator()
    {
        (Block? block, TxReceipt[] _) = InsertBlock();

        _storage.ClearCache();
        Assert.That(_storage.TryGetReceiptsIterator(block.Number, block.Hash!, out ReceiptsIterator iterator), Is.True);
        Assert.That(iterator.TryGetNext(out TxReceiptStructRef receiptStructRef), Is.True);
        Assert.That(receiptStructRef.LogsRlp.ToArray(), Is.Not.Empty);
        Assert.That(receiptStructRef.Logs, Is.Null.Or.Empty);

        Assert.That(iterator.TryGetNext(out _), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Log_recovery_takes_the_stored_transaction_hash_without_reading_the_body()
    {
        Assume.That(useCompactReceipts, "Only compact receipts store the transaction hashes.");
        (Block block, TxReceipt[] _) = InsertBlock();
        _storage.ClearCache();
        _blockStore.ClearReceivedCalls();

        Assert.That(ReadStoredReceipts(block)[0], Is.EqualTo(ReceiptArrayStorageDecoder.CompactEncodingWithTxHashes));
        RecoveredLogFields recovered = RecoverFirstLogFields(block);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(recovered.TransactionHash, Is.EqualTo(block.Transactions[0].Hash));
            Assert.That(recovered.Index, Is.EqualTo(0));
            Assert.That(recovered.BlockNumber, Is.EqualTo(block.Number));
            Assert.That(recovered.BlockHash, Is.EqualTo(block.Hash));
        }

        _blockStore.DidNotReceiveWithAnyArgs().GetReceiptRecoveryBlock(default, default!);
    }

    [TestCase(true, ReceiptArrayStorageDecoder.CompactEncodingWithTxHashes)]
    [TestCase(false, ReceiptArrayStorageDecoder.CompactEncoding)]
    [MaxTime(Timeout.MaxTestTime)]
    public void Stores_the_transaction_hashes_only_when_configured(bool storeTxHashes, int marker)
    {
        Assume.That(useCompactReceipts, "Only compact receipts store the transaction hashes.");
        _receiptConfig.StoreTxHashes = storeTxHashes;
        CreateStorage(configuredDecoder: true);

        (Block block, TxReceipt[] _) = InsertBlock();

        Assert.That(ReadStoredReceipts(block)[0], Is.EqualTo(marker));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Log_recovery_reads_the_body_for_compact_receipts_without_transaction_hashes()
    {
        Assume.That(useCompactReceipts, "Only compact receipts store the transaction hashes.");
        (Block block, TxReceipt[] receipts) = InsertBlock();
        _storage.ClearCache();
        TxReceipt[] withoutHashes = receipts.Select(static r => new TxReceipt(r) { TxHash = null }).ToArray();
        WriteStoredReceipts(block, _decoder.EncodeAsBytes(withoutHashes, RlpBehaviors.Storage));
        _blockStore.ClearReceivedCalls();

        Assert.That(ReadStoredReceipts(block)[0], Is.EqualTo(ReceiptArrayStorageDecoder.CompactEncoding));
        Assert.That(RecoverFirstLogFields(block).TransactionHash, Is.EqualTo(block.Transactions[0].Hash));
        _blockStore.Received().GetReceiptRecoveryBlock(block.Number, block.Hash!);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Adds_and_retrieves_receipts_for_block_with_iterator_from_cache_after_get()
    {
        (Block? block, TxReceipt[]? receipts) = InsertBlock();

        _storage.ClearCache();
        _storage.Get(block);
        Assert.That(_storage.TryGetReceiptsIterator(0, block.Hash!, out ReceiptsIterator iterator), Is.True);
        Assert.That(iterator.TryGetNext(out TxReceiptStructRef receiptStructRef), Is.True);
        Assert.That(receiptStructRef.LogsRlp.ToArray(), Is.Empty);
        receiptStructRef.Logs.AssertEquivalentTo(receipts.First().Logs);
        Assert.That(iterator.TryGetNext(out _), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Should_handle_inserting_null_receipts()
    {
        Block block = Build.A.Block.WithReceiptsRoot(TestItem.KeccakA).TestObject;
        _storage.Insert(block, null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void HasBlock_should_returnFalseForMissingHash() =>
        Assert.That(_storage.HasBlock(0, Keccak.Compute("missing-value")), Is.False);

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void HasBlock_should_returnTrueForKnownHash()
    {
        (Block? block, TxReceipt[] _) = InsertBlock();
        Assert.That(_storage.HasBlock(block.Number, block.Hash!), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void EnsureCanonical_should_change_tx_blockhash(
        [Values(false, true)] bool ensureCanonical,
        [Values(false, true)] bool isFinalized)
    {
        (Block block, TxReceipt[] receipts) = InsertBlock(isFinalized: isFinalized);
        Assert.That(_storage.FindBlockHash(receipts[0].TxHash!), Is.EqualTo(block.Hash!));

        Block anotherBlock = Build.A.Block
            .WithTransactions(block.Transactions)
            .WithReceiptsRoot(TestItem.KeccakA)
            .WithExtraData(new byte[] { 1 })
            .TestObject;

        Assert.That(anotherBlock.Hash, Is.Not.EqualTo(block.Hash!));
        _storage.Insert(anotherBlock, new[] { Build.A.Receipt.TestObject }, ensureCanonical);
        _blockTree.FindBlockHash(anotherBlock.Number).Returns(anotherBlock.Hash);

        Hash256? findBlockHash = _storage.FindBlockHash(receipts[0].TxHash!);
        if (ensureCanonical)
        {
            Assert.That(findBlockHash, Is.EqualTo(anotherBlock.Hash!));
        }
        else
        {
            Assert.That(findBlockHash, Is.Not.EqualTo(anotherBlock.Hash!));
        }
    }

    [Test]
    public void EnsureCanonical_should_use_blockNumber_if_finalized()
    {
        (Block block, TxReceipt[] receipts) = InsertBlock(isFinalized: true);
        Span<byte> txHashBytes = receipts[0].TxHash!.Bytes;
        if (_receiptConfig.CompactTxIndex)
        {
            Assert.That(_receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[txHashBytes], Is.EqualTo(Rlp.Encode(block.Number).Bytes));
        }
        else
        {
            Assert.That(_receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[txHashBytes], Is.Not.Null);
        }
    }

    [Test]
    public void When_TxLookupLimitIs_MaxValue_DoNotIndexTxHash()
    {
        _receiptConfig.TxLookupLimit = ulong.MaxValue;
        CreateStorage();
        (Block block, TxReceipt[] receipts) = InsertBlock(isFinalized: true);
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));
        Assert.That(() => _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[receipts[0].TxHash!.Bytes], Is.Null.After(100, 10));
    }

    [TestCase(1ul, false)]
    [TestCase(10ul, false)]
    [TestCase(11ul, true)]
    public void Should_only_prune_index_tx_hashes_if_blockNumber_is_bigger_than_lookupLimit(ulong blockNumber, bool willPruneOldIndices)
    {
        _receiptConfig.TxLookupLimit = 10ul;
        CreateStorage();
        _blockTree.BlockAddedToMain +=
            Raise.EventWith(new BlockReplacementEventArgs(Build.A.Block.WithNumber(blockNumber).TestObject));
        Assert.That(() => _blockTree.ReceivedCalls()
            .Where(static call => call.GetMethodInfo().Name.EndsWith(nameof(_blockTree.FindBlockHash))),
            willPruneOldIndices ? Is.Not.Empty.After(10000, 50) : Is.Empty.After(100, 10));
    }

    [Test]
    public void When_HeadBlockIsFarAhead_DoNotIndexTxHash()
    {
        _receiptConfig.TxLookupLimit = 1000ul;
        CreateStorage();
        (Block block, TxReceipt[] receipts) = InsertBlock(isFinalized: true, headNumber: 1001ul);
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block));
        Assert.That(() => _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[receipts[0].TxHash!.Bytes], Is.Null.After(100, 10));
    }

    [Test]
    public void When_NewHeadBlock_DoNotRemove_TxIndex_WhenTxIsInOtherBlockNumber()
    {
        CreateStorage();

        Transaction tx = Build.A.Transaction.SignedAndResolved().TestObject;

        Block b1a = Build.A.Block.WithNumber(1).TestObject;
        Block b1b = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;
        Block b2a = Build.A.Block.WithNumber(2).WithParent(b1a).WithTransactions(tx).TestObject;
        Block b2b = Build.A.Block.WithNumber(2).WithParent(b1b).TestObject;

        InsertBlock(b1a);
        InsertBlock(b1b);
        InsertBlock(b2a);
        InsertBlock(b2b);

        // b1a
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(b1a, null));

        // b1b
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(b1b, b1a));

        // b2a
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(b1a, b1b));
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(b2a, null));

        // b2b
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(b1b, b1a));
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(b2b, b2a));

        Assert.That(_storage.FindBlockHash(tx.Hash!), Is.EqualTo(b1b.Hash!));
    }

    [Test]
    public async Task When_NewHeadBlock_Remove_TxIndex_OfRemovedBlock_Unless_ItsAlsoInNewBlock()
    {
        _receiptConfig.CompactTxIndex = useCompactReceipts;
        CreateStorage();
        (Block block, _) = InsertBlock();
        Block block2 = Build.A.Block
            .WithParent(block)
            .WithNumber(2)
            .WithTransactions(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyC).TestObject)
            .TestObject;
        _blockTree.FindBestSuggestedHeader().Returns(block2.Header);
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block2));

        if (_receiptConfig.CompactTxIndex)
        {
            Assert.That(_receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[block.Transactions[0].Hash!.Bytes], Is.EqualTo(Rlp.Encode(block.Number).Bytes));
        }
        else
        {
            Assert.That(_receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[block.Transactions[0].Hash!.Bytes], Is.EqualTo(block.Hash!.Bytes.ToArray()));
        }

        Block block3 = Build.A.Block
            .WithNumber(1)
            .WithTransactions(block2.Transactions)
            .WithExtraData(new byte[1])
            .TestObject;
        Block block4 = Build.A.Block
            .WithNumber(2)
            .WithTransactions(block.Transactions)
            .WithExtraData(new byte[1])
            .TestObject;
        _blockTree.FindBestSuggestedHeader().Returns(block4.Header);
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block3, block));
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(block4, block2));

        await Task.Delay(100);
        if (_receiptConfig.CompactTxIndex)
        {
            Assert.That(_receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[block4.Transactions[0].Hash!.Bytes], Is.EqualTo(Rlp.Encode(block4.Number).Bytes));
        }
        else
        {
            Assert.That(_receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[block4.Transactions[0].Hash!.Bytes], Is.EqualTo(block4.Hash!.Bytes.ToArray()));
        }
    }

    [Test]
    public void When_NewHeadBlock_ClearOldTxIndex_And_KeepsReceipts()
    {
        _receiptConfig.TxLookupLimit = 1000ul;
        CreateStorage();
        (Block block, TxReceipt[] receipts) = InsertBlock();

        Span<byte> txHashBytes = receipts[0].TxHash!.Bytes;
        if (_receiptConfig.CompactTxIndex)
        {
            Assert.That(_receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[txHashBytes], Is.EqualTo(Rlp.Encode(block.Number).Bytes));
        }
        else
        {
            Assert.That(_receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[txHashBytes], Is.Not.Null);
        }

        Block newHead = Build.A.Block.WithNumber(_receiptConfig.TxLookupLimit.Value + 1ul).TestObject;
        _blockTree.FindBestSuggestedHeader().Returns(newHead.Header);
        _blockTree.BlockAddedToMain += Raise.EventWith(new BlockReplacementEventArgs(newHead));

        // The old index is pruned in a Task.Run, which the thread pool of a loaded runner can start well after a second.
        Assert.That(
            () => _receiptsDb.GetColumnDb(ReceiptsColumns.Transactions)[receipts[0].TxHash!.Bytes],
            Is.Null.After(10000, 50)
            );
        Assert.That(_storage.HasBlock(receipts[0].BlockNumber, receipts[0].BlockHash!));
    }

    [TestCase(false, 5ul, TestName = "Insert tracks the lowest inserted block")]
    [TestCase(true, ulong.MaxValue, TestName = "InsertForMigration leaves the pointer to the migration")]
    public void Migration_pointer_is_advanced_only_by_the_normal_insert_path(bool viaMigration, ulong expectedMigratedBlockNumber)
    {
        const ulong blockNumber = 5;
        _storage.MigratedBlockNumber = ulong.MaxValue;

        (Block block, TxReceipt[] receipts) = PrepareBlock(Build.A.Block
            .WithNumber(blockNumber)
            .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject)
            .WithReceiptsRoot(TestItem.KeccakA)
            .TestObject);

        if (viaMigration)
        {
            ((IReceiptMigrationStore)_storage).InsertForMigration(block, receipts);
        }
        else
        {
            _storage.Insert(block, receipts);
        }

        Assert.That(_storage.MigratedBlockNumber, Is.EqualTo(expectedMigratedBlockNumber),
            "the migration owns the pointer under parallel out-of-order inserts, so only the normal Insert path may advance it");
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Deriving_from_state_skips_bodies_on_the_processing_path()
    {
        _receiptConfig.DeriveFromState = true;
        CreateStorage();

        Block block = ProcessBlock();

        Assert.That(BodyIsPersisted(block), Is.False);
    }

    // Pre-EIP-658 receipts carry a post-transaction state root that re-execution cannot reproduce.
    [TestCase(true, false)]
    [TestCase(false, true)]
    public void Deriving_from_state_still_stores_pre_eip658_bodies(bool eip658Enabled, bool expectPersisted)
    {
        _specProvider.GenesisSpec = eip658Enabled ? Byzantium.Instance : SpuriousDragon.Instance;
        _specProvider.NextForkSpec = _specProvider.GenesisSpec;
        _receiptConfig.DeriveFromState = true;
        CreateStorage();

        Block block = ProcessBlock();

        Assert.That(BodyIsPersisted(block), Is.EqualTo(expectPersisted));
    }

    // Migration deletes the legacy key after re-inserting, so a skipped write there would destroy the bodies.
    [Test]
    public void Deriving_from_state_does_not_skip_bodies_outside_block_processing([Values] bool viaMigration)
    {
        _receiptConfig.DeriveFromState = true;
        CreateStorage();

        (Block block, TxReceipt[] receipts) = PrepareBlock();
        if (viaMigration)
            ((IReceiptMigrationStore)_storage).InsertForMigration(block, receipts);
        else
            _storage.Insert(block, receipts);

        Assert.That(BodyIsPersisted(block), Is.True);
    }

    // A body skipped while capture is unhealthy is lost once the block leaves the in-memory tier.
    [Test]
    [MaxTime(Timeout.MaxTestTime)]
    public void Deriving_from_state_stores_bodies_without_healthy_history_capture([Values(false, null)] bool? captureHealthy)
    {
        _receiptConfig.DeriveFromState = true;
        if (captureHealthy is { } health)
        {
            CreateStorage(captureHealthy: health);
        }
        else
        {
            _decoder = new ReceiptArrayStorageDecoder(useCompactReceipts);
            _storage = new PersistentReceiptStorage(
                _receiptsDb, _specProvider, _receiptsRecovery, _blockTree, _blockStore, _receiptConfig, _decoder)
            { MigratedBlockNumber = 0 };
        }

        Block block = ProcessBlock();

        Assert.That(BodyIsPersisted(block), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Storing_bodies_is_the_default()
    {
        Block block = ProcessBlock();

        Assert.That(BodyIsPersisted(block), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Deriving_from_state_persists_retained_bodies_when_capture_stops()
    {
        _receiptConfig.DeriveFromState = true;
        CreateStorage();
        IStateHistoryCaptureStatus captureStatus = _captureStatus;
        Block block = ProcessBlock();
        Assert.That(BodyIsPersisted(block), Is.False, "precondition: the body is skipped while capture is healthy");

        captureStatus.CaptureDisabled += Raise.Event<Action>();

        Assert.That(BodyIsPersisted(block), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Deriving_from_state_drops_retained_bodies_once_the_watermark_covers_them()
    {
        _receiptConfig.DeriveFromState = true;
        CreateStorage();
        IStateHistoryCaptureStatus captureStatus = _captureStatus;
        Block block = ProcessBlock();

        captureStatus.WatermarkAdvanced += Raise.Event<Action<ulong>>((ulong)block.Number);
        captureStatus.CaptureDisabled += Raise.Event<Action>();

        Assert.That(BodyIsPersisted(block), Is.False);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Deriving_from_state_keeps_retaining_bodies_above_the_watermark()
    {
        _receiptConfig.DeriveFromState = true;
        CreateStorage();
        IStateHistoryCaptureStatus captureStatus = _captureStatus;
        Block block = ProcessBlock();

        captureStatus.WatermarkAdvanced += Raise.Event<Action<ulong>>((ulong)block.Number - 1);
        captureStatus.CaptureDisabled += Raise.Event<Action>();

        Assert.That(BodyIsPersisted(block), Is.True);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Deriving_from_state_stores_bodies_once_the_retention_cap_is_reached()
    {
        _receiptConfig.DeriveFromState = true;
        CreateStorage();

        Transaction tx = Build.A.Transaction.SignedAndResolved().TestObject;
        Block InsertBlockNumber(ulong number)
        {
            Block block = Build.A.Block.WithNumber(number).WithTransactions(tx).WithReceiptsRoot(TestItem.KeccakA).TestObject;
            _storage.InsertDeferred(block, [Build.A.Receipt.WithCalculatedBloom().TestObject], _specProvider.GetSpec((ForkActivation)number));
            return block;
        }

        Block firstRetained = InsertBlockNumber(1);
        for (ulong number = 2; number <= PersistentReceiptStorage.MaxRetainedBodies; number++)
        {
            InsertBlockNumber(number);
        }
        Block overflow = InsertBlockNumber(PersistentReceiptStorage.MaxRetainedBodies + 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BodyIsPersisted(overflow), Is.True, "at the cap the body must write through");
            Assert.That(BodyIsPersisted(firstRetained), Is.False, "below the cap the skip must keep applying");
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Deriving_from_state_stores_bodies_once_the_retention_byte_cap_is_reached()
    {
        _receiptConfig.DeriveFromState = true;
        CreateStorage();

        // One shared array: the estimate counts it per block, so the test never holds that much.
        const int LogDataBytes = 8 * 1024 * 1024;
        int blocksToSaturate = (int)(PersistentReceiptStorage.MaxRetainedBytes / LogDataBytes);
        Assert.That(blocksToSaturate, Is.LessThan(PersistentReceiptStorage.MaxRetainedBodies),
            "precondition: the byte cap must bind before the block cap on log-heavy blocks");

        LogEntry heavyLog = new(TestItem.AddressA, new byte[LogDataBytes], [TestItem.KeccakA]);
        Transaction tx = Build.A.Transaction.SignedAndResolved().TestObject;
        Block InsertHeavyBlock(ulong number)
        {
            Block block = Build.A.Block.WithNumber(number).WithTransactions(tx).WithReceiptsRoot(TestItem.KeccakA).TestObject;
            _storage.InsertDeferred(block, [Build.A.Receipt.WithLogs(heavyLog).WithCalculatedBloom().TestObject],
                _specProvider.GetSpec((ForkActivation)number));
            return block;
        }

        Block firstRetained = InsertHeavyBlock(1);
        for (ulong number = 2; number <= (ulong)blocksToSaturate; number++)
        {
            InsertHeavyBlock(number);
        }

        Assert.That(_storage.RetainedBytes, Is.GreaterThanOrEqualTo(PersistentReceiptStorage.MaxRetainedBytes),
            "precondition: retention must be saturated by bytes at this point");

        Block overflow = InsertHeavyBlock((ulong)blocksToSaturate + 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BodyIsPersisted(overflow), Is.True, "at the byte cap the body must write through");
            Assert.That(BodyIsPersisted(firstRetained), Is.False, "below the cap the skip must keep applying");
        }
    }

    // A removal that drops a body without subtracting its bytes latches the skip off once the drift exceeds the cap.
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Removing_receipts_keeps_the_retained_byte_count_in_step()
    {
        _receiptConfig.DeriveFromState = true;
        CreateStorage();
        IStateHistoryCaptureStatus captureStatus = _captureStatus;

        Block block = ProcessBlock();
        Assert.That(_storage.RetainedBytes, Is.GreaterThan(0), "precondition: the skipped body is retained");

        _storage.RemoveReceipts(block);
        long afterRemoval = _storage.RetainedBytes;

        captureStatus.CaptureDisabled += Raise.Event<Action>();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterRemoval, Is.Zero, "removal must subtract the body's bytes");
            Assert.That(_storage.RetainedBodyCount, Is.Zero);
            Assert.That(BodyIsPersisted(block), Is.False, "a removed body must not be resurrected by the disable flush");
        }
    }

    // The disable flush may drain between the health check that skipped the write and the retention add.
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Deriving_from_state_persists_a_body_skipped_concurrently_with_the_disable()
    {
        _receiptConfig.DeriveFromState = true;
        CreateStorage();
        _captureStatus.CaptureHealthy.Returns(true, false);

        Block block = ProcessBlock();

        Assert.That(BodyIsPersisted(block), Is.True);
    }

    private Block ProcessBlock()
    {
        (Block block, TxReceipt[] receipts) = PrepareBlock();
        _storage.InsertDeferred(block, receipts, _specProvider.GetSpec((ForkActivation)block.Header.Number));
        return block;
    }

    // A fresh instance has an empty receipts cache, so this reads through to the column.
    private bool BodyIsPersisted(Block block)
    {
        CreateStorage();
        return _storage.HasBlock(block.Number, block.Hash!);
    }

    private (Block block, TxReceipt[] receipts) PrepareBlock(Block? block = null, bool isFinalized = false, ulong? headNumber = null)
    {
        block ??= Build.A.Block
            .WithNumber(1)
            .WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject)
            .WithReceiptsRoot(TestItem.KeccakA)
            .TestObject;

        _blockTree.FindBlock(block.Hash!).Returns(block);
        _blockTree.FindBlock(block.Number).Returns(block);
        _blockTree.FindHeader(block.Number).Returns(block.Header);
        _blockTree.FindBlockHash(block.Number).Returns(block.Hash);
        _blockStore.GetReceiptRecoveryBlock(block.Number, block.Hash!).Returns(new ReceiptRecoveryBlock(block));
        if (isFinalized)
        {
            BlockHeader farHead = Build.A.BlockHeader
                .WithNumber(Reorganization.MaxDepth + 5)
                .TestObject;
            _blockTree.FindBestSuggestedHeader().Returns(farHead);
        }

        if (headNumber is not null)
        {
            BlockHeader farHead = Build.A.BlockHeader
                .WithNumber(headNumber.Value)
                .TestObject;
            _blockTree.FindBestSuggestedHeader().Returns(farHead);
        }

        TxReceipt[] receipts = Array.Empty<TxReceipt>();
        if (block.Transactions.Length == 1)
        {
            receipts = [Build.A.Receipt.WithCalculatedBloom().TestObject];
        }
        return (block, receipts);
    }

    private readonly record struct RecoveredLogFields(Hash256? TransactionHash, int Index, ulong BlockNumber, Hash256 BlockHash);

    private RecoveredLogFields RecoverFirstLogFields(Block block)
    {
        Assert.That(_storage.TryGetReceiptsIterator(block.Number, block.Hash!, out ReceiptsIterator iterator), Is.True);
        try
        {
            Assert.That(iterator.TryGetNext(out TxReceiptStructRef receipt), Is.True);
            iterator.RecoverLogFieldsIfNeeded(ref receipt, out Hash256? transactionHash);
            return new RecoveredLogFields(transactionHash, receipt.Index, receipt.BlockNumber, receipt.BlockHash.ToCommitment());
        }
        finally
        {
            iterator.Dispose();
        }
    }

    private byte[] ReadStoredReceipts(Block block) =>
        _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Get(StoredReceiptsKey(block))!;

    private void WriteStoredReceipts(Block block, byte[] value) =>
        _receiptsDb.GetColumnDb(ReceiptsColumns.Blocks).Set(StoredReceiptsKey(block), value);

    private static byte[] StoredReceiptsKey(Block block)
    {
        byte[] key = new byte[40];
        KeyValueStoreExtensions.GetBlockNumPrefixedKey(block.Number, block.Hash!, key);
        return key;
    }

    private (Block block, TxReceipt[] receipts) InsertBlock(Block? block = null, bool isFinalized = false, ulong? headNumber = null, WriteFlags writeFlags = WriteFlags.None)
    {
        (block, TxReceipt[] receipts) = PrepareBlock(block, isFinalized, headNumber);
        _storage.Insert(block, receipts, writeFlags: writeFlags);
        _receiptsRecovery.TryRecover(new ReceiptRecoveryBlock(block), receipts);

        return (block, receipts);
    }

}
