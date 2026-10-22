// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Facade.Simulate;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Simulate;

[Parallelizable(ParallelScope.All)]
public class SimulateDictionaryBlockStoreTests
{
    [Test]
    public void HasBlock_finds_block_present_only_in_base_store()
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;
        BlockStore baseStore = new(new MemDb());
        baseStore.Insert(block);

        SimulateDictionaryBlockStore store = new(baseStore);

        Assert.That(store.HasBlock(block.Number, block.Hash!), Is.True);
    }

    [Test]
    public void HasBlock_finds_block_cached_in_overlay()
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;
        SimulateDictionaryBlockStore store = new(new BlockStore(new MemDb()));
        store.Cache(block);

        Assert.That(store.HasBlock(block.Number, block.Hash!), Is.True);
    }

    [Test]
    public void HasBlock_returns_false_when_block_is_in_neither_store()
    {
        Block block = Build.A.Block.WithNumber(1).TestObject;
        SimulateDictionaryBlockStore store = new(new BlockStore(new MemDb()));

        Assert.That(store.HasBlock(block.Number, block.Hash!), Is.False);
    }
}
