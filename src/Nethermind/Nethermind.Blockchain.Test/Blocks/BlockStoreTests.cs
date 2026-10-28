// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Caching;
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
    public void Test_getReceiptRecoveryBlock_produce_same_transaction_as_normal_get([Values] bool native, [Values] bool legacy)
    {
        using TestMemDb db = native ? new PinnedTestDb() : new TestMemDb();
        BlockStore store = new(db);

        Block block = Build.A.Block.WithNumber(1)
            .WithTransactions(3, MainnetSpecProvider.Instance)
            .TestObject;

        if (legacy) db[block.Hash!.Bytes] = new BlockDecoder().Encode(block).Bytes;
        else store.Insert(block);

        ReceiptRecoveryBlock retrieved = store.GetReceiptRecoveryBlock(block.Number, block.Hash!)!.Value;
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retrieved.Header, Is.EqualTo(block.Header).UsingBlockHeaderComparer());
                Assert.That(retrieved.TransactionCount, Is.EqualTo(block.Transactions.Length));
                for (int i = 0; i < retrieved.TransactionCount; i++)
                {
                    block.Transactions[i].Data = Array.Empty<byte>();
                    Assert.That(retrieved.GetNextTransaction(), Is.EqualTo(block.Transactions[i]).UsingTransactionComparer());
                }
            }
        }
        finally
        {
            retrieved.Dispose();
            retrieved.Dispose();
        }
        if (db is PinnedTestDb pinned)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pinned.NativeReads, Is.EqualTo(legacy ? 2 : 1));
                Assert.That(pinned.Released, Is.EqualTo(1));
                Assert.That(pinned.LiveHandles, Is.Zero);
            }
        }
    }

    [Test]
    public void Test_getReceiptRecoveryBlock_returns_null_when_block_not_in_db([Values] bool native, [Values] bool empty)
    {
        using TestMemDb db = native ? new PinnedTestDb() : new TestMemDb();
        BlockStore store = new(db);

        Block block = Build.A.Block.WithNumber(1).TestObject;

        if (empty)
        {
            db[Bytes.Concat(block.Number.ToBigEndianByteArray(), block.Hash!.BytesToArray())] = [];
            db[block.Hash.Bytes] = [];
        }
        ReceiptRecoveryBlock? result = store.GetReceiptRecoveryBlock(block.Number, block.Hash!);

        Assert.That(result, Is.Null);
        if (db is PinnedTestDb pinned)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pinned.NativeReads, Is.EqualTo(2));
                Assert.That(pinned.Released, Is.EqualTo(empty ? 2 : 0));
                Assert.That(pinned.LiveHandles, Is.Zero);
            }
        }
    }

    [Test]
    public void Recovery_empty_numbered_value_releases_its_handle_before_legacy_fallback()
    {
        using PinnedTestDb db = new();
        BlockStore store = new(db);
        Block block = Build.A.Block.WithNumber(1).TestObject;
        db[Bytes.Concat(block.Number.ToBigEndianByteArray(), block.Hash!.BytesToArray())] = [];
        db[block.Hash.Bytes] = new BlockDecoder().Encode(block).Bytes;
        ReceiptRecoveryBlock recovery = store.GetReceiptRecoveryBlock(block.Number, block.Hash)!.Value;
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(recovery.Hash, Is.EqualTo(block.Hash));
                Assert.That(db.Released, Is.EqualTo(1));
                Assert.That(db.LiveHandles, Is.EqualTo(1));
            }
        }
        finally
        {
            recovery.Dispose();
        }
        Assert.That(db.LiveHandles, Is.Zero);
    }

    [Test]
    public void Recovery_transaction_is_owned_after_the_block_lease_ends([Values] bool zeroHandle)
    {
        using TestMemDb db = zeroHandle ? new NativeTestMemDb() : new PinnedTestDb();
        BlockStore store = new(db);
        Transaction tx = Build.A.Transaction.WithData([1, 2, 3]).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Block block = Build.A.Block.WithNumber(1).WithTransactions(tx).TestObject;
        byte[] expected = TxDecoder.Instance.Encode(tx).Bytes;
        store.Insert(block);
        ReceiptRecoveryBlock recovery = store.GetReceiptRecoveryBlock(block.Number, block.Hash!)!.Value;
        Transaction escaped;
        try
        {
            // A zero-handle native store lends a movable array; recovery must own a copy, not its pointer.
            foreach (byte[] value in db.Values) value.AsSpan().Fill(0);
            escaped = recovery.GetTransaction(0);
        }
        finally
        {
            recovery.Dispose();
            recovery.Dispose();
        }
        Assert.That(TxDecoder.Instance.Encode(escaped).Bytes, Is.EqualTo(expected));
        if (db is PinnedTestDb pinned)
        {
            Assert.That(() => recovery.GetTransaction(0), Throws.InstanceOf<ObjectDisposedException>());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pinned.Released, Is.EqualTo(1));
                Assert.That(pinned.LiveHandles, Is.Zero);
            }
        }
    }

    [Test]
    public void Recovery_decode_failure_releases_the_native_handle([Values] bool nullBlock)
    {
        using PinnedTestDb db = new();
        BlockStore store = new(db);
        Block block = Build.A.Block.WithNumber(1).TestObject;
        db[Bytes.Concat(block.Number.ToBigEndianByteArray(), block.Hash!.BytesToArray())] = nullBlock ? [0xc0] : [0x01];

        if (nullBlock) Assert.That(store.GetReceiptRecoveryBlock(block.Number, block.Hash), Is.Null);
        else Assert.That(() => store.GetReceiptRecoveryBlock(block.Number, block.Hash), Throws.InstanceOf<RlpException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.Released, Is.EqualTo(1));
            Assert.That(db.LiveHandles, Is.Zero);
        }
    }

    [Test]
    public async Task Recovery_cache_and_pending_paths_do_not_acquire_native_handles([Values] bool pending)
    {
        using PinnedTestDb db = new();
        StatePersistenceBarrier barrier = new();
        await using DeferredBlockDataWriter writer = DeferredWriteTestHelpers.ManualWriter(barrier);
        BlockStore store = new(db, deferredWriter: writer, persistenceBarrier: barrier);
        Block block = Build.A.Block.WithNumber(1).WithTransactions(Build.A.Transaction.SignedAndResolved().TestObject).TestObject;
        if (pending) store.InsertDeferred(block);
        else store.Cache(block);

        ReceiptRecoveryBlock recovery = store.GetReceiptRecoveryBlock(block.Number, block.Hash!)!.Value;
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(recovery.GetTransaction(0), Is.SameAs(block.Transactions[0]));
                Assert.That(db.NativeReads, Is.Zero);
                Assert.That(db.LiveHandles, Is.Zero);
            }
        }
        finally
        {
            recovery.Dispose();
        }
    }

    private sealed class PinnedTestDb : TestMemDb, IReadOnlyNativeKeyValueStore, IDisposable
    {
        private readonly Dictionary<nint, byte[]> _pins = [];
        public int NativeReads { get; private set; }
        public int Released { get; private set; }
        public int LiveHandles => _pins.Count;

        public ReadOnlySpan<byte> GetNativeSlice(scoped ReadOnlySpan<byte> key, out nint handle, ReadFlags flags = ReadFlags.None)
        {
            NativeReads++;
            handle = 0;
            byte[]? value = Get(key, flags);
            if (value is null) return null;
            byte[] bytes = (byte[])value.Clone();
            GCHandle pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            handle = GCHandle.ToIntPtr(pin);
            _pins.Add(handle, bytes);
            return bytes;
        }

        public void DangerousReleaseHandle(nint handle)
        {
            if (!_pins.Remove(handle, out byte[]? bytes)) throw new InvalidOperationException("Native handle released twice.");
            bytes.AsSpan().Fill(0xff);
            GCHandle.FromIntPtr(handle).Free();
            Released++;
        }

        public new void Dispose()
        {
            foreach (nint handle in _pins.Keys) GCHandle.FromIntPtr(handle).Free();
            _pins.Clear();
            base.Dispose();
        }
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
