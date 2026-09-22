// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.State.Repositories;
using NUnit.Framework;

namespace Nethermind.Store.Test.Repositories;

public class ChainLevelInfoRepositoryTests
{
    [Test]
    public void Canonical_batch_preserves_suggested_level_durability_until_it_commits()
    {
        using TestMemDb db = new();
        ChainLevelInfoRepository repository = new(db);
        Queue<Action> writes = new();
        ChainLevelInfo suggested = new(false, new BlockInfo(TestItem.KeccakA, 1));
        ChainLevelInfo canonical = new(true, new BlockInfo(TestItem.KeccakA, 1) { WasProcessed = true });
        using (BatchWrite batch = repository.StartDeferredBatch())
            repository.PersistLevelDeferred(1, suggested, writes.Enqueue, batch);

        using (BatchWrite batch = repository.StartBatch())
        {
            repository.PersistLevel(1, canonical, batch);
            writes.Dequeue()();
            AssertChainLevelInfo(new ChainLevelInfoRepository(db).LoadLevel(1)!, suggested);
        }

        AssertChainLevelInfo(new ChainLevelInfoRepository(db).LoadLevel(1)!, canonical);
    }

    [Test]
    public void Deferred_writer_can_run_while_the_producer_holds_the_batch_lock()
    {
        using MemDb db = new();
        ChainLevelInfoRepository repository = new(db);
        ChainLevelInfo level = new(false, new BlockInfo(TestItem.KeccakA, 1));
        using (BatchWrite batch = repository.StartDeferredBatch())
            repository.PersistLevelDeferred(1, level,
                write => Task.Run(write).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(), batch);

        AssertChainLevelInfo(new ChainLevelInfoRepository(db).LoadLevel(1)!, level);
    }

    [Test]
    public void Failed_deferred_level_write_remains_visible_and_retryable()
    {
        using TestMemDb db = new();
        ChainLevelInfoRepository repository = new(db);
        Queue<Action> writes = new();
        ChainLevelInfo level = new(false, new BlockInfo(TestItem.KeccakA, 1));
        using (BatchWrite batch = repository.StartDeferredBatch())
            repository.PersistLevelDeferred(1, level, writes.Enqueue, batch);
        Action write = writes.Dequeue();
        db.WriteFunc = (_, _) => throw new InvalidOperationException("write failed");
        Assert.Throws<InvalidOperationException>(() => write());
        ((IClearableCache)repository).ClearCache();
        AssertChainLevelInfo(repository.LoadLevel(1)!, level);

        db.WriteFunc = null;
        write();

        AssertChainLevelInfo(new ChainLevelInfoRepository(db).LoadLevel(1)!, level);
    }

    [Test]
    public void Deferred_level_survives_cache_eviction_until_written()
    {
        using MemDb db = new();
        ChainLevelInfoRepository repository = new(db);
        Queue<Action> writes = new();
        ChainLevelInfo level = new(false, new BlockInfo(TestItem.KeccakA, 1));
        using (BatchWrite batch = repository.StartDeferredBatch())
            repository.PersistLevelDeferred(1, level, writes.Enqueue, batch);
        ((IClearableCache)repository).ClearCache();

        Assert.That(new ChainLevelInfoRepository(db).LoadLevel(1), Is.Null);
        AssertChainLevelInfo(repository.LoadLevel(1)!, level);
        using ArrayPoolListRef<ulong> numbers = new(1, 1UL);
        using IOwnedReadOnlyList<ChainLevelInfo> levels = repository.MultiLoadLevel(numbers);
        AssertChainLevelInfo(levels[0]!, level);

        writes.Dequeue()();

        AssertChainLevelInfo(new ChainLevelInfoRepository(db).LoadLevel(1)!, level);
    }

    [Test]
    public void Later_level_update_or_delete_supersedes_pending_write([Values] bool delete, [Values] bool useBatch)
    {
        using MemDb db = new();
        ChainLevelInfoRepository repository = new(db);
        Queue<Action> writes = new();
        ChainLevelInfo oldLevel = new(false, new BlockInfo(TestItem.KeccakA, 1));
        ChainLevelInfo newLevel = new(true, new BlockInfo(TestItem.KeccakB, 2));
        using (BatchWrite batch = repository.StartDeferredBatch())
            repository.PersistLevelDeferred(1, oldLevel, writes.Enqueue, batch);
        using (BatchWrite batch = useBatch ? repository.StartBatch() : null)
        {
            if (delete) repository.Delete(1, batch);
            else repository.PersistLevel(1, newLevel, batch);
        }
        writes.Dequeue()();

        ChainLevelInfo actual = new ChainLevelInfoRepository(db).LoadLevel(1);
        if (delete) Assert.That(actual, Is.Null);
        else AssertChainLevelInfo(actual!, newLevel);
    }

    [Test]
    public void TestMultiGet()
    {
        ChainLevelInfoRepository repository = new(new MemDb());

        ChainLevelInfo level1 = new(false, new BlockInfo(TestItem.KeccakA, 0));
        ChainLevelInfo level10 = new(false, new BlockInfo(TestItem.KeccakB, 0));

        {
            using BatchWrite _ = repository.StartBatch();
            repository.PersistLevel(1, level1);
            repository.PersistLevel(10, level10);
        }

        using IOwnedReadOnlyList<ChainLevelInfo> levels = repository.MultiLoadLevel(new ArrayPoolListRef<ulong>(2, 1UL, 10UL));
        AssertChainLevelInfo(levels[0], level1);
        AssertChainLevelInfo(levels[1], level10);
    }

    [Test]
    public void TestClearCache_removes_cached_levels()
    {
        MemDb db = new();
        ChainLevelInfoRepository repository = new(db);

        ChainLevelInfo level1 = new(false, new BlockInfo(TestItem.KeccakA, 0));

        {
            using BatchWrite _ = repository.StartBatch();
            repository.PersistLevel(1, level1);
        }

        // Load level to populate cache
        ChainLevelInfo loaded = repository.LoadLevel(1);
        AssertChainLevelInfo(loaded, level1);

        // Clear DB but level should still be in cache
        db.Clear();
        loaded = repository.LoadLevel(1);
        AssertChainLevelInfo(loaded, level1);

        // Clear cache - level should no longer be retrievable
        (repository as IClearableCache)?.ClearCache();
        Assert.That(repository.LoadLevel(1), Is.Null);
    }

    private static void AssertChainLevelInfo(ChainLevelInfo actual, ChainLevelInfo expected)
    {
        Assert.That(actual, Is.Not.Null);
        if (actual is null)
        {
            return;
        }

        Assert.Multiple(() =>
        {
            Assert.That(actual.HasBlockOnMainChain, Is.EqualTo(expected.HasBlockOnMainChain));
            Assert.That(actual.BlockInfos, Is.EqualTo(expected.BlockInfos));
        });
    }
}
