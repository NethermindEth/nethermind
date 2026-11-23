// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Facade.Simulate;
using Nethermind.Serialization.Rlp;
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

    [Test]
    public void GetRlps_reads_overlay_and_base_blocks_and_null_for_missing()
    {
        Block inBase = Build.A.Block.WithNumber(1).TestObject;
        Block cached = Build.A.Block.WithNumber(2).TestObject;
        Block missing = Build.A.Block.WithNumber(3).TestObject;
        BlockStore baseStore = new(new MemDb());
        baseStore.Insert(inBase);
        SimulateDictionaryBlockStore store = new(baseStore);
        store.Cache(cached);

        using PooledSegmentedBuffer destination = new(1024);
        ReadOnlyMemory<byte>?[] rlps = new ReadOnlyMemory<byte>?[3];
        ((IBlockStore)store).GetRlps(
            [(missing.Number, missing.Hash!.ValueHash256), (inBase.Number, inBase.Hash!.ValueHash256), (cached.Number, cached.Hash!.ValueHash256)],
            destination,
            rlps);

        BlockDecoder decoder = new();
        Assert.That(Array.ConvertAll(rlps, static rlp => rlp?.ToArray()), Is.EqualTo(new[] { null, decoder.Encode(inBase), decoder.Encode(cached) }));
    }
}
