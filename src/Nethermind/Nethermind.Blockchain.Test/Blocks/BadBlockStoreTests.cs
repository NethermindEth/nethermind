// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
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

        ((ISortedKeyValueStore)badBlockStore).GetAllValues().Should().HaveCount(toAdd.Count);
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

        ((ISortedKeyValueStore)badBlockStore).GetAllValues().Count().Should().Be(2);
    }
}
