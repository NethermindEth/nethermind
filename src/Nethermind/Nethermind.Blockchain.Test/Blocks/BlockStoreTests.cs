// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices.JavaScript;
using FluentAssertions;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Blocks;

public class BlockStoreTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void Test_can_insert_get_and_remove_blocks(bool cached)
    {
        TestMemDb db = new TestMemDb();
        BlockStore store = new BlockStore(db);

        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.Insert(block);

        Block? retrieved = store.Get(block.Hash, cached);
        retrieved.Should().BeEquivalentTo(block);

        store.Delete(block.Hash);

        store.Get(block.Hash, cached).Should().BeNull();
    }

    [Test]
    public void Test_can_set_and_get_metadata()
    {
        TestMemDb db = new TestMemDb();
        BlockStore store = new BlockStore(db);

        byte[] key = new byte[] { 1, 2, 3 };
        byte[] value = new byte[] { 4, 5, 6 };

        store.SetMetadata(key, value);
        store.GetMetadata(key).Should().BeEquivalentTo(value);
    }

    [Test]
    public void Test_when_cached_does_not_touch_db_on_next_get()
    {
        TestMemDb db = new TestMemDb();
        BlockStore store = new BlockStore(db);

        Block block = Build.A.Block.WithNumber(1).TestObject;
        store.Insert(block);

        Block? retrieved = store.Get(block.Hash, true);
        retrieved.Should().BeEquivalentTo(block);

        db.Clear();

        retrieved = store.Get(block.Hash, true);
        retrieved.Should().BeEquivalentTo(block);
    }

    [Test]
    public void Test_getReceiptRecoveryBlock_produce_same_transaction_as_normal_get()
    {
        TestMemDb db = new TestMemDb();
        BlockStore store = new BlockStore(db);

        Block block = Build.A.Block.WithNumber(1)
            .WithTransactions(3, MainnetSpecProvider.Instance)
            .TestObject;

        store.Insert(block);

        ReceiptRecoveryBlock retrieved = store.GetReceiptRecoveryBlock(block.Hash).Value;
        retrieved.Should().NotBeNull();

        retrieved.Header.Should().BeEquivalentTo(block.Header);
        retrieved.TransactionCount.Should().Be(block.Transactions.Length);

        for (int i = 0; i < retrieved.TransactionCount; i++)
        {
            block.Transactions[i].Data = Array.Empty<byte>();
            retrieved.GetNextTransaction().Should().BeEquivalentTo(block.Transactions[i]);
        }
    }
}
