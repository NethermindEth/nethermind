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

public class BadBlockStoreTests
{
    [Test]
    public void Test_CanInsert()
    {
        BadBlockStore badBlockStore = new BadBlockStore(new TestMemDb(), 10);

        List<Block> toAdd = new()
        {
            Build.A.Block.WithNumber(1).TestObject,
            Build.A.Block.WithNumber(2).TestObject,
            Build.A.Block.WithNumber(3).TestObject,
        };

        foreach (Block block in toAdd)
        {
            badBlockStore.Insert(block);
        }

        badBlockStore.GetAll().Select(block =>
        {
            block.EncodedSize = null;
            return block;
        }).Should().BeEquivalentTo(toAdd);
    }

    [Test]
    public void Test_LimitStoredBlock()
    {
        BadBlockStore badBlockStore = new BadBlockStore(new TestMemDb(), 2);

        List<Block> toAdd = new()
        {
            Build.A.Block.WithNumber(1).TestObject,
            Build.A.Block.WithNumber(2).TestObject,
            Build.A.Block.WithNumber(3).TestObject,
        };

        foreach (Block block in toAdd)
        {
            badBlockStore.Insert(block);
        }

        badBlockStore.GetAll().Count().Should().Be(2);
    }
}
