// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
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
