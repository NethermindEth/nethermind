// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Serialization.Rlp.Eip7928;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Blocks;

[Parallelizable(ParallelScope.All)]
public class BadBlockStoreTests
{
    [Test]
    public void Test_CanInsert()
    {
        BadBlockStore badBlockStore = new(new TestMemDb(), 10);

        List<Block> toAdd =
        [
            Build.A.Block.WithNumber(1).TestObject,
            Build.A.Block.WithNumber(2).TestObject,
            Build.A.Block.WithNumber(3).TestObject,
        ];

        foreach (Block block in toAdd)
        {
            badBlockStore.Insert(block);
        }

        Assert.That(badBlockStore.GetAll(), Is.EquivalentTo(toAdd).UsingBlockComparer());
        Assert.That(badBlockStore.GetAll().Select(static b => b.EncodedBlockAccessList), Has.All.Null);
    }

    [Test]
    public void Insert_keeps_suggested_block_access_list([Values] bool preEncoded)
    {
        BadBlockStore badBlockStore = new(new TestMemDb(), 10);
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithBalanceChanges(new BalanceChange(1, 1)).TestObject)
            .TestObject;
        byte[] encoded = BlockAccessListDecoder.EncodeToBytes(bal);
        Block block = preEncoded
            ? Build.A.Block.WithNumber(1).WithEncodedBlockAccessList(encoded).TestObject
            : Build.A.Block.WithNumber(1).WithBlockAccessList(bal).TestObject;

        badBlockStore.Insert(block);

        Block stored = badBlockStore.GetAll().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Hash, Is.EqualTo(block.Hash));
            Assert.That(stored.EncodedBlockAccessList, Is.EqualTo(encoded));
            Assert.That(stored.BlockAccessList, Is.Null);
        }
    }

    [Test]
    public void Insert_drops_block_access_list_above_size_limit([Values] bool aboveLimit)
    {
        const int limit = 100;
        BadBlockStore badBlockStore = new(new TestMemDb(), 10, maxBlockAccessListSize: limit);
        byte[] bal = new byte[aboveLimit ? limit + 1 : limit];
        Block block = Build.A.Block.WithNumber(1).WithEncodedBlockAccessList(bal).TestObject;

        badBlockStore.Insert(block);

        Block stored = badBlockStore.GetAll().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Hash, Is.EqualTo(block.Hash));
            Assert.That(stored.EncodedBlockAccessList, aboveLimit ? Is.Null : Is.EqualTo(bal));
        }
    }

    [Test]
    public void Insert_drops_oldest_block_access_lists_above_budget()
    {
        const int balSize = 100;
        BadBlockStore badBlockStore = new(new TestMemDb(), 10, maxBlockAccessListSize: balSize, blockAccessListBudget: 2 * balSize);

        for (ulong number = 1; number <= 4; number++)
        {
            byte[] bal = new byte[balSize];
            bal[0] = (byte)number;
            badBlockStore.Insert(Build.A.Block.WithNumber(number).WithEncodedBlockAccessList(bal).TestObject);
        }

        Block[] stored = badBlockStore.GetAll().ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stored.Select(static b => b.Number), Is.EqualTo(new ulong[] { 1, 2, 3, 4 }));
            Assert.That(stored.Select(static b => b.EncodedBlockAccessList?[0]), Is.EqualTo(new byte?[] { null, null, 3, 4 }));
        }
    }

    [Test]
    public void Test_LimitStoredBlock()
    {
        BadBlockStore badBlockStore = new(new TestMemDb(), 2);

        List<Block> toAdd =
        [
            Build.A.Block.WithNumber(1).TestObject,
            Build.A.Block.WithNumber(2).TestObject,
            Build.A.Block.WithNumber(3).TestObject,
        ];

        foreach (Block block in toAdd)
        {
            badBlockStore.Insert(block);
        }

        int count = 0;
        foreach (Block _ in badBlockStore.GetAll())
        {
            count++;
        }

        Assert.That(count, Is.EqualTo(2));
    }

    [Test]
    public void Test_LimitStoredBlock_bounds_by_entry_count_not_byte_size()
    {
        BadBlockStore badBlockStore = new(new ByteSizeMemDb(), 2);

        List<Block> toAdd =
        [
            Build.A.Block.WithNumber(1).TestObject,
            Build.A.Block.WithNumber(2).TestObject,
            Build.A.Block.WithNumber(3).TestObject,
        ];

        foreach (Block block in toAdd)
        {
            badBlockStore.Insert(block);
        }

        int count = 0;
        foreach (Block _ in badBlockStore.GetAll())
        {
            count++;
        }

        Assert.That(count, Is.EqualTo(2));
    }

    private sealed class ByteSizeMemDb : MemDb
    {
        public override IDbMeta.DbMetric GatherMetric() => new() { Size = Count * 1024 };
    }
}
