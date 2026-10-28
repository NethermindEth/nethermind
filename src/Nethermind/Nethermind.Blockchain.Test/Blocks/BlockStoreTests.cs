// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
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

    [Test]
    public void GetForReplay_decodes_a_block_once_and_gives_each_caller_its_own_block()
    {
        TestMemDb db = new();
        BlockStore store = new(db);
        Block block = Build.A.Block.WithNumber(1).WithTransactions(Build.A.Transaction.SignedAndResolved().TestObjectNTimes(3)).TestObject;
        store.Insert(block);

        Block first = store.GetForReplay(block.Number, block.Hash!)!;
        first.ExecutionRequests = [[1]];
        Block second = store.GetForReplay(block.Number, block.Hash!)!;

        byte[] key = Bytes.Concat(block.Number.ToBigEndianByteArray(), block.Hash!.BytesToArray());
        db.KeyWasRead(key);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Hash, Is.EqualTo(block.Hash));
            Assert.That(first.Transactions.Select(static tx => tx.Hash), Is.EqualTo(block.Transactions.Select(static tx => tx.Hash)));
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.Body, Is.SameAs(first.Body));
            Assert.That(second.ExecutionRequests, Is.Null);
            Assert.That(second.EncodedSize, Is.EqualTo(first.EncodedSize).And.Not.Null);
        }
    }

    [Test]
    public void GetForReplay_does_not_serve_a_deleted_block([Values] bool byRange)
    {
        TestMemDb db = new();
        BlockStore store = new(db);
        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.Insert(block);
        Assert.That(store.GetForReplay(block.Number, block.Hash!), Is.Not.Null);

        if (byRange) store.DeleteRange(block.Number, block.Number + 1);
        else store.Delete(block.Number, block.Hash!);

        Assert.That(store.GetForReplay(block.Number, block.Hash!), Is.Null);
    }

    [Test]
    public void GetForReplay_decodes_again_after_ClearCache()
    {
        TestMemDb db = new();
        BlockStore store = new(db);
        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.Insert(block);

        store.GetForReplay(block.Number, block.Hash!);
        ((IClearableCache)store).ClearCache();
        store.GetForReplay(block.Number, block.Hash!);

        db.KeyWasRead(Bytes.Concat(block.Number.ToBigEndianByteArray(), block.Hash!.BytesToArray()), 2);
    }

    [Test]
    public void GetForReplay_keeps_at_most_ReplayCacheSize_blocks()
    {
        const int count = BlockStore.ReplayCacheSize * 4;
        TestMemDb db = new();
        BlockStore store = new(db);
        Block[] blocks = new Block[count];
        for (int i = 0; i < count; i++)
        {
            blocks[i] = Build.A.Block.WithNumber((ulong)i + 1).TestObject;
            store.Insert(blocks[i]);
            store.GetForReplay(blocks[i].Number, blocks[i].Hash!);
        }

        long readsBefore = db.ReadsCount;
        foreach (Block block in blocks)
        {
            Assert.That(store.GetForReplay(block.Number, block.Hash!), Is.EqualTo(block).UsingBlockComparer());
        }

        Assert.That(db.ReadsCount - readsBefore, Is.GreaterThanOrEqualTo(count - BlockStore.ReplayCacheSize));
    }

    [Test]
    public void GetForReplay_serves_concurrent_callers_the_same_content()
    {
        const int callers = 64;
        TestMemDb db = new();
        BlockStore store = new(db);
        Block block = Build.A.Block.WithNumber(1).WithTransactions(Build.A.Transaction.SignedAndResolved().TestObjectNTimes(3)).TestObject;
        store.Insert(block);

        Block?[] results = new Block?[callers];
        Parallel.For(0, callers, i => results[i] = store.GetForReplay(block.Number, block.Hash!));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(results.Select(static result => result?.Hash), Is.All.EqualTo(block.Hash));
            Assert.That(new HashSet<Block?>(results, ReferenceEqualityComparer.Instance), Has.Count.EqualTo(callers));
        }
    }
}
