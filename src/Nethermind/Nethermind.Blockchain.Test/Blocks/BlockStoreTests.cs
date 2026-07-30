// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Blocks;

[Parallelizable(ParallelScope.All)]
public class BlockStoreTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void Test_can_insert_get_and_remove_blocks(bool cached)
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

    [TestCase(true)]
    [TestCase(false)]
    public void Test_can_get_block_that_was_stored_with_hash(bool cached)
    {
        TestMemDb db = new();
        BlockStore store = new(db);

        Block block = Build.A.Block.WithNumber(1).TestObject;
        db[block.Hash!.Bytes] = new BlockDecoder().Encode(block).Bytes;

        Block? retrieved = store.Get(block.Number, block.Hash!, RlpBehaviors.None, cached);
        Assert.That(retrieved, Is.EqualTo(block).UsingBlockComparer());
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
