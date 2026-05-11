// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
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

        Assert.That(
            badBlockStore.GetAll().Select(static block => block.Hash),
            Is.EquivalentTo(toAdd.Select(static block => block.Hash)));
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

        Assert.That(badBlockStore.GetAll().Count(), Is.EqualTo(2));
    }
}
