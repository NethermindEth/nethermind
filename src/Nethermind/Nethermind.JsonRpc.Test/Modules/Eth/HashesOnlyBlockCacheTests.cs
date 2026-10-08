// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.JsonRpc.Modules.Eth;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

[Parallelizable(ParallelScope.All)]
public class HashesOnlyBlockCacheTests
{
    private const int TransactionCount = 100;

    [Test]
    public void Add_PastTheBudget_EvictsTheOldestBlock()
    {
        HashesOnlyBlock first = CreateBlock();
        HashesOnlyBlockCache cache = new(2 * first.EstimatedSize);
        cache.Add(TestItem.KeccakA, first, cache.Generation);
        cache.Add(TestItem.KeccakB, CreateBlock(), cache.Generation);

        cache.Add(TestItem.KeccakC, CreateBlock(), cache.Generation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryGet(TestItem.KeccakA, out _), Is.False, "the oldest block is evicted");
            Assert.That(cache.TryGet(TestItem.KeccakB, out _), Is.True, "the second block stays");
            Assert.That(cache.TryGet(TestItem.KeccakC, out _), Is.True, "the new block is added");
            Assert.That(cache.Bytes, Is.EqualTo(2 * first.EstimatedSize), "the cache holds two blocks' worth");
        }
    }

    [Test]
    public void Add_PastTheBudget_GivesABlockReadSinceItWasAddedASecondChance()
    {
        HashesOnlyBlock first = CreateBlock();
        HashesOnlyBlockCache cache = new(2 * first.EstimatedSize);
        cache.Add(TestItem.KeccakA, first, cache.Generation);
        cache.Add(TestItem.KeccakB, CreateBlock(), cache.Generation);
        Assert.That(cache.TryGet(TestItem.KeccakA, out _), Is.True, "precondition: the oldest block is read");

        cache.Add(TestItem.KeccakC, CreateBlock(), cache.Generation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryGet(TestItem.KeccakA, out _), Is.True, "the read block survives");
            Assert.That(cache.TryGet(TestItem.KeccakB, out _), Is.False, "the unread block is evicted in its place");
        }
    }

    [Test]
    public void Add_BlockLargerThanTheBudget_IsNotCached()
    {
        HashesOnlyBlock block = CreateBlock();
        HashesOnlyBlockCache cache = new(block.EstimatedSize - 1);

        cache.Add(TestItem.KeccakA, block, cache.Generation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryGet(TestItem.KeccakA, out _), Is.False, "a block above the whole budget is skipped");
            Assert.That(cache.Bytes, Is.Zero, "nothing is accounted");
        }
    }

    [Test]
    public void Add_BlockReadBeforeAClear_IsNotCached()
    {
        HashesOnlyBlockCache cache = new();
        cache.Add(TestItem.KeccakA, CreateBlock(), cache.Generation);
        long generation = cache.Generation;

        cache.Clear();
        cache.Add(TestItem.KeccakB, CreateBlock(), generation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryGet(TestItem.KeccakA, out _), Is.False, "the clear drops what was cached");
            Assert.That(cache.TryGet(TestItem.KeccakB, out _), Is.False, "a block read before the clear stays out");
            Assert.That(cache.Bytes, Is.Zero, "nothing is accounted after the clear");
        }
    }

    [Test]
    public void EstimatedSize_Block_CountsThirtyTwoBytesPerTransaction()
    {
        HashesOnlyBlock empty = new(Build.A.Block.TestObject, []);

        Assert.That(CreateBlock().EstimatedSize - empty.EstimatedSize, Is.EqualTo(TransactionCount * ValueHash256.MemorySize),
            "the hashes are stored flat, 32 bytes each");
    }

    private static HashesOnlyBlock CreateBlock() => new(Build.A.Block.TestObject, new ValueHash256[TransactionCount]);
}
