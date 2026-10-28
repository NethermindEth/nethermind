// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Blocks;

[Parallelizable(ParallelScope.All)]
public class BlockStoreTests
{
    [Test]
    public void Test_can_insert_get_and_remove_blocks([Values] bool cached)
    {
        TestMemDb db = new();
        BlockStore store = new(db);

        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.Insert(block);

        Block? retrieved = store.Get(block.Number, block.Hash!, RlpBehaviors.None, cached);
        Assert.That(retrieved, Is.EqualTo(block).UsingBlockComparer());

        store.Delete(block.Number, block.Hash!);

        Assert.That(store.Get(block.Number, block.Hash!, RlpBehaviors.None, cached), Is.Null);
    }

    [Test]
    public void Test_insert_would_pass_in_write_flag()
    {
        TestMemDb db = new();
        BlockStore store = new(db);

        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.Insert(block, WriteFlags.DisableWAL);

        byte[] key = Bytes.Concat(block.Number.ToBigEndianByteArray(), block.Hash!.BytesToArray());
        db.KeyWasWrittenWithFlags(key, WriteFlags.DisableWAL);
    }

    [Test]
    public void Test_can_get_block_that_was_stored_with_hash([Values] bool cached)
    {
        TestMemDb db = new();
        BlockStore store = new(db);

        Block block = Build.A.Block.WithNumber(1).TestObject;
        db[block.Hash!.Bytes] = new BlockDecoder().Encode(block).Bytes;

        // Probe before the read so the cached case cannot be satisfied by an entry Get itself populated.
        Assert.That(store.HasBlock(block.Number, block.Hash!), Is.True);

        Block? retrieved = store.Get(block.Number, block.Hash!, RlpBehaviors.None, cached);
        Assert.That(retrieved, Is.EqualTo(block).UsingBlockComparer());
    }

    [Test]
    public void Test_cached_block_that_was_never_written_is_not_reported_as_stored()
    {
        TestMemDb db = new();
        BlockStore store = new(db);

        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.Cache(block);

        // The cache is a bounded read accelerator, so a hit there says the block can be read right now, not that
        // it is stored. Callers deciding whether they still have to download a body key off HasBlock, and an
        // eviction after a true answer loses the body for good.
        Assert.That(store.Get(block.Number, block.Hash!), Is.Not.Null);
        Assert.That(store.HasBlock(block.Number, block.Hash!), Is.False);
    }

    [Test]
    public async Task Get_WhenBlockIsCachedOrPending_PreservesEncodedSize([Values] bool deferred)
    {
        await using DeferredBlockDataWriter writer = DeferredWriteTestHelpers.ManualWriter();
        ContainerBuilder builder = new();
        builder.RegisterModule(new TestNethermindModule());
        builder.RegisterInstance(writer).As<IDeferredBlockDataWriter>().ExternallyOwned();
        using IContainer container = builder.Build();
        IBlockStore store = container.Resolve<IBlockStore>();
        Block block = Build.A.Block.WithNumber(1).WithEncodedSize(1234).TestObject;

        if (deferred)
        {
            store.InsertDeferred(block);
        }
        else
        {
            store.Cache(block);
        }

        Block? retrieved = store.Get(block.Number, block.Hash!);

        Assert.That(retrieved, Is.Not.Null, "The stored snapshot must be available before a deferred write is flushed.");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(retrieved, Is.Not.SameAs(block), "The store must return a snapshot rather than the live block.");
            Assert.That(retrieved!.EncodedSize, Is.EqualTo(1234), "The snapshot must retain the size computed before storage.");
            Assert.That(writer.QueuedCount, Is.EqualTo(deferred ? 1 : 0), "The pending block must remain queued until the writer is pumped.");
        }
    }

    [Test]
    public void Test_can_set_and_get_metadata()
    {
        TestMemDb db = new();
        BlockStore store = new(db);

        byte[] key = [1, 2, 3];
        byte[] value = [4, 5, 6];

        store.SetMetadata(key, value);
        Assert.That(store.GetMetadata(key), Is.EqualTo(value));
    }

    [Test]
    public void Test_when_cached_does_not_touch_db_on_next_get()
    {
        TestMemDb db = new();
        BlockStore store = new(db);

        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.Insert(block);

        Block? retrieved = store.Get(block.Number, block.Hash!, RlpBehaviors.None, true);
        Assert.That(retrieved, Is.EqualTo(block).UsingBlockComparer());

        db.Clear();

        retrieved = store.Get(block.Number, block.Hash!, RlpBehaviors.None, true);
        retrieved!.EncodedSize = null;
        Assert.That(retrieved, Is.EqualTo(block).UsingBlockComparer());
    }

    [Test]
    public void Test_getReceiptRecoveryBlock_produce_same_transaction_as_normal_get()
    {
        TestMemDb db = new();
        BlockStore store = new(db);

        Block block = Build.A.Block.WithNumber(1)
            .WithTransactions(3, MainnetSpecProvider.Instance)
            .TestObject;

        store.Insert(block);

        ReceiptRecoveryBlock retrieved = store.GetReceiptRecoveryBlock(block.Number, block.Hash!)!.Value;

        Assert.That(retrieved.Header, Is.EqualTo(block.Header).UsingBlockHeaderComparer());
        Assert.That(retrieved.TransactionCount, Is.EqualTo(block.Transactions.Length));

        for (int i = 0; i < retrieved.TransactionCount; i++)
        {
            block.Transactions[i].Data = Array.Empty<byte>();
            Assert.That(retrieved.GetNextTransaction(), Is.EqualTo(block.Transactions[i]).UsingTransactionComparer());
        }
    }

    [Test]
    public void Recovery_rlp_reads_are_reused_with_independent_mutable_results([Values] bool warm, [Values] bool legacyKey)
    {
        TestMemDb db = new();
        BlockStore store = new(db);
        Block block = Build.A.Block.WithNumber(1).WithExtraData([7, 8])
            .WithTransactions(
                Build.A.Transaction.WithNonce(1).WithData([1, 2, 3]).SignedAndResolved().TestObject,
                Build.A.Transaction.WithNonce(2).WithData([4, 5, 6]).SignedAndResolved().TestObject)
            .TestObject;
        if (legacyKey) db[block.Hash!.Bytes] = new BlockDecoder().Encode(block).Bytes;
        else store.Insert(block);
        if (warm) AssertRecovery(store, block);

        ReceiptRecoveryBlock first = store.GetReceiptRecoveryBlock(block.Number, block.Hash!)!.Value;
        long reads = db.ReadsCount;
        ReceiptRecoveryBlock second = store.GetReceiptRecoveryBlock(block.Number, block.Hash!)!.Value;
        try
        {
            Assert.That(db.ReadsCount, Is.EqualTo(reads), "the second cursor must reuse the encoded body");
            Assert.That(second.Header, Is.Not.SameAs(first.Header));
            Transaction transaction = first.GetNextTransaction();
            MemoryMarshal.AsMemory(transaction.Data).Span[0] = 99;
            first.Header.Number = 99;
            first.Header.ExtraData[0] = 99;

            Assert.That(second.GetNextTransaction().Data.ToArray(), Is.EqualTo(block.Transactions[0].Data.ToArray()));
            Assert.That(first.GetNextTransactionHash(), Is.EqualTo(block.Transactions[1].Hash));
            Assert.That(second.GetNextTransactionHash(), Is.EqualTo(block.Transactions[1].Hash));
            AssertRecovery(store, block);
            Assert.That(db.ReadsCount, Is.EqualTo(reads), "cache hits must not acquire another database lease");
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }
    }

    [Test]
    public void Recovery_rlp_is_invalidated_by_replacement([Values] bool deferred)
    {
        TestMemDb db = new();
        BlockStore store = new(db);
        Block block = Build.A.Block.WithNumber(1).WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject).TestObject;
        store.Insert(block);
        AssertRecovery(store, block);
        Block replacement = new(block.Header, [], []);

        if (deferred) store.InsertDeferred(replacement);
        else store.Insert(replacement);

        AssertRecovery(store, replacement);
    }

    [Test]
    public void Cache_takes_precedence_and_invalidates_recovery_rlp()
    {
        TestMemDb db = new();
        BlockStore store = new(db);
        Block block = Build.A.Block.WithNumber(1).WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject).TestObject;
        store.Insert(block);
        AssertRecovery(store, block);
        Block replacement = new(block.Header, [], []);

        store.Cache(replacement);

        AssertRecovery(store, replacement);
        Assert.That(PrivateField<System.Collections.IDictionary>(store, "_entries").Count, Is.Zero);
    }

    private static readonly string[] RecoveryInvalidations =
    [
        nameof(BlockStore.Delete), nameof(BlockStore.DeleteRange), nameof(BlockStore.DeleteRanges), nameof(IClearableCache.ClearCache)
    ];

    [Test]
    public void Recovery_rlp_is_invalidated_by_removal([ValueSource(nameof(RecoveryInvalidations))] string invalidation)
    {
        TestMemDb db = new();
        BlockStore store = new(db);
        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.Insert(block);
        AssertRecovery(store, block);

        InvalidateRecovery(store, db, block, invalidation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.GetReceiptRecoveryBlock(block.Number, block.Hash!), Is.Null);
            Assert.That(store.HasBlock(block.Number, block.Hash!), Is.False);
        }
    }

    [Test]
    public void Recovery_rlp_does_not_refill_after_removal_during_a_read([ValueSource(nameof(RecoveryInvalidations))] string invalidation)
    {
        TestMemDb db = new();
        BlockStore store = new(db);
        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.Insert(block);
        byte[] beforeRemoval = new BlockDecoder().Encode(block).Bytes;
        db.ReadFunc = _ =>
        {
            db.ReadFunc = null;
            InvalidateRecovery(store, db, block, invalidation);
            return beforeRemoval;
        };

        AssertRecovery(store, block);

        Assert.That(store.GetReceiptRecoveryBlock(block.Number, block.Hash!), Is.Null);
    }

    [Test]
    public void Recovery_rlp_does_not_refill_after_replacement_during_a_read()
    {
        TestMemDb db = new();
        BlockStore store = new(db);
        Block block = Build.A.Block.WithNumber(1).WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject).TestObject;
        store.Insert(block);
        Block replacement = new(block.Header, [], []);
        byte[] beforeReplacement = new BlockDecoder().Encode(block).Bytes;
        db.ReadFunc = _ =>
        {
            db.ReadFunc = null;
            store.Insert(replacement);
            return beforeReplacement;
        };

        AssertRecovery(store, block);

        AssertRecovery(store, replacement);
    }

    [Test]
    public void Recovery_rlp_evicts_the_least_recently_used_entry([Values] bool byteBound)
    {
        TestMemDb db = new();
        BlockStore store = new(db);
        Block[] blocks = [Build.A.Block.WithNumber(1).TestObject, Build.A.Block.WithNumber(2).TestObject, Build.A.Block.WithNumber(3).TestObject];
        foreach (Block block in blocks) store.Insert(block);
        int byteCapacity = 2 * new BlockDecoder().Encode(blocks[0]).Bytes.Length;
        SetPrivateField(store, byteBound ? "_byteCapacity" : "_capacity", byteBound ? byteCapacity : 2);
        AssertRecovery(store, blocks[0]);
        AssertRecovery(store, blocks[1]);
        AssertRecovery(store, blocks[0]);
        AssertRecovery(store, blocks[2]);
        long reads = db.ReadsCount;

        AssertRecovery(store, blocks[0]);
        Assert.That(db.ReadsCount, Is.EqualTo(reads));
        AssertRecovery(store, blocks[1]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.ReadsCount, Is.EqualTo(reads + 1));
            Assert.That(PrivateField<System.Collections.IDictionary>(store, "_entries").Count, Is.EqualTo(2));
            if (byteBound) Assert.That(PrivateField<int>(store, "_bytes"), Is.LessThanOrEqualTo(byteCapacity));
        }
    }

    [Test]
    public void Oversize_recovery_rlp_is_not_cached()
    {
        TestMemDb db = new();
        BlockStore store = new(db);
        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.Insert(block);
        SetPrivateField(store, "_byteCapacity", new BlockDecoder().Encode(block).Bytes.Length - 1);

        AssertRecovery(store, block);
        AssertRecovery(store, block);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.ReadsCount, Is.EqualTo(2));
            Assert.That(PrivateField<System.Collections.IDictionary>(store, "_entries").Count, Is.Zero);
            Assert.That(PrivateField<int>(store, "_bytes"), Is.Zero);
        }
    }

    [Test]
    public void Recovery_rlp_cache_never_reports_an_unstored_body_as_present()
    {
        TestMemDb db = new();
        BlockStore store = new(db);
        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.Insert(block);
        AssertRecovery(store, block);
        db.Clear();

        AssertRecovery(store, block);
        Assert.That(store.HasBlock(block.Number, block.Hash!), Is.False);
    }

    [Test]
    public void Recovery_rlp_miss_transfers_the_owner_but_hits_do_not_acquire_one()
    {
        RecoveryMemoryDb db = new();
        BlockStore store = new(db);
        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.Insert(block);
        ReceiptRecoveryBlock recovery = store.GetReceiptRecoveryBlock(block.Number, block.Hash!)!.Value;
        try
        {
            Assert.That(db.DisposedOwners, Is.Zero);
            AssertRecovery(store, block);
            Assert.That(db.CreatedOwners, Is.EqualTo(1));
        }
        finally
        {
            recovery.Dispose();
        }

        Assert.That(db.DisposedOwners, Is.EqualTo(1));
    }

    [Test]
    public void Recovery_rlp_releases_the_owner_when_decoding_does_not_return_a_block([Values] bool emptyList)
    {
        RecoveryMemoryDb db = new();
        BlockStore store = new(db);
        Block block = Build.A.Block.WithNumber(1).TestObject;
        db.ReadFunc = _ => emptyList ? [Rlp.EmptyListByte] : [0xf8, 0xff];

        for (int i = 0; i < 2; i++)
        {
            if (emptyList) Assert.That(store.GetReceiptRecoveryBlock(block.Number, block.Hash!), Is.Null);
            else Assert.Throws<RlpException>(() => store.GetReceiptRecoveryBlock(block.Number, block.Hash!));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.CreatedOwners, Is.EqualTo(2));
            Assert.That(db.DisposedOwners, Is.EqualTo(2));
        }
    }

    [Test]
    public void Recovery_rlp_releases_the_owner_when_snapshot_copy_throws_after_decoding()
    {
        RecoveryMemoryDb db = new() { FailSnapshot = true };
        BlockStore store = new(db);
        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.Insert(block);

        Assert.Throws<InvalidOperationException>(() => store.GetReceiptRecoveryBlock(block.Number, block.Hash!));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.CreatedOwners, Is.EqualTo(1));
            Assert.That(db.DisposedOwners, Is.EqualTo(1));
        }
    }

    private static void AssertRecovery(BlockStore store, Block expected)
    {
        ReceiptRecoveryBlock? result = store.GetReceiptRecoveryBlock(expected.Number, expected.Hash!);
        Assert.That(result, Is.Not.Null);
        ReceiptRecoveryBlock recovery = result!.Value;
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(recovery.Header, Is.EqualTo(expected.Header).UsingBlockHeaderComparer());
                Assert.That(recovery.TransactionCount, Is.EqualTo(expected.Transactions.Length));
            }
            foreach (Transaction transaction in expected.Transactions)
            {
                Assert.That(recovery.GetNextTransactionHash(), Is.EqualTo(transaction.Hash));
            }
        }
        finally
        {
            recovery.Dispose();
        }
    }

    private static void InvalidateRecovery(BlockStore store, TestMemDb db, Block block, string invalidation)
    {
        switch (invalidation)
        {
            case nameof(BlockStore.Delete):
                store.Delete(block.Number, block.Hash!);
                break;
            case nameof(BlockStore.DeleteRange):
                store.DeleteRange(block.Number, block.Number + 1);
                break;
            case nameof(BlockStore.DeleteRanges):
                store.DeleteRanges([(block.Number, block.Number + 1)]);
                break;
            case nameof(IClearableCache.ClearCache):
                db.Clear();
                ((IClearableCache)store).ClearCache();
                break;
        }
    }

    private static EncodedRlpCache<ValueHash256> RecoveryCache(BlockStore store) =>
        (EncodedRlpCache<ValueHash256>)typeof(BlockStore).GetField("_recoveryRlp", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(store)!;

    private static T PrivateField<T>(BlockStore store, string name) =>
        (T)typeof(EncodedRlpCache<ValueHash256>).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(RecoveryCache(store))!;

    private static void SetPrivateField(BlockStore store, string name, int value) =>
        typeof(EncodedRlpCache<ValueHash256>).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(RecoveryCache(store), value);

    private sealed class RecoveryMemoryDb : TestMemDb, IReadOnlyKeyValueStore
    {
        public int CreatedOwners { get; private set; }
        public int DisposedOwners { get; private set; }
        public bool FailSnapshot { get; init; }

        MemoryManager<byte>? IReadOnlyKeyValueStore.GetOwnedMemory(ReadOnlySpan<byte> key, ReadFlags flags)
        {
            byte[]? bytes = Get(key, flags);
            if (bytes is null) return null;
            CreatedOwners++;
            return new Owner((byte[])bytes.Clone(), this);
        }

        private sealed class Owner(byte[] bytes, RecoveryMemoryDb db) : MemoryManager<byte>
        {
            private int _reads;

            public override Span<byte> GetSpan()
            {
                // Memory creation and decoding each access the owner before the snapshot copy does.
                if (++_reads == 3 && db.FailSnapshot) throw new InvalidOperationException("Snapshot copy failed.");
                return bytes;
            }

            public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
            public override void Unpin() { }
            protected override void Dispose(bool disposing) => db.DisposedOwners++;
        }
    }

    [Test]
    public void Test_getReceiptRecoveryBlock_returns_null_when_block_not_in_db()
    {
        TestMemDb db = new();
        BlockStore store = new(db);

        Block block = Build.A.Block.WithNumber(1).TestObject;

        ReceiptRecoveryBlock? result = store.GetReceiptRecoveryBlock(block.Number, block.Hash!);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void Test_DeleteRanges_takes_every_range_and_keeps_the_heights_between()
    {
        TestMemDb db = new();
        BlockStore store = new(db);

        Block[] blocks = new Block[6];
        for (ulong number = 1; number <= 5; number++)
        {
            blocks[number] = Build.A.Block.WithNumber(number).TestObject;
            store.Insert(blocks[number]);
        }

        store.Get(blocks[2].Number, blocks[2].Hash!, RlpBehaviors.None, shouldCache: true);

        store.DeleteRanges([(1, 3), (4, 5)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(blocks[1].Number, blocks[1].Hash!), Is.Null);
            Assert.That(store.Get(blocks[2].Number, blocks[2].Hash!), Is.Null, "a cached block inside a deleted range must stop being served");
            Assert.That(store.Get(blocks[3].Number, blocks[3].Hash!), Is.Not.Null, "the height between the ranges survives");
            Assert.That(store.Get(blocks[4].Number, blocks[4].Hash!), Is.Null);
            Assert.That(store.Get(blocks[5].Number, blocks[5].Hash!), Is.Not.Null, "the upper bound is exclusive");
        }
    }

    [Test]
    public void Test_DeleteRanges_with_an_empty_range_still_probes_range_delete_support()
    {
        BlockStore store = new(Substitute.For<IDb>());

        Assert.That(() => store.DeleteRanges([(0, 0)]), Throws.InstanceOf<NotSupportedException>(),
            "an empty range is the pruner's capability probe, so it must reach the store instead of being skipped");
    }

    [Test]
    public void Test_ClearCache_removes_cached_blocks()
    {
        TestMemDb db = new();
        BlockStore store = new(db);

        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.Insert(block);

        // Populate cache
        Block? retrieved = store.Get(block.Number, block.Hash!, RlpBehaviors.None, shouldCache: true);
        Assert.That(retrieved, Is.EqualTo(block).UsingBlockComparer());

        // Clear the DB but block should still be in cache
        db.Clear();
        retrieved = store.Get(block.Number, block.Hash!, RlpBehaviors.None, shouldCache: true);
        Assert.That(retrieved, Is.Not.Null);

        // Clear the cache - now block should not be retrievable
        (store as IClearableCache)?.ClearCache();
        retrieved = store.Get(block.Number, block.Hash!, RlpBehaviors.None, shouldCache: true);
        Assert.That(retrieved, Is.Null);
    }

}
