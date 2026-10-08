// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Repositories;
using NUnit.Framework;

namespace Nethermind.Store.Test.Repositories;

public class ChainLevelInfoRepositoryTests
{
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

    [Test]
    public void LoadLevel_serves_a_header_cache_sized_window_from_cache()
    {
        const int levelCount = 256 + 16;
        MemDb db = new();
        ChainLevelInfoRepository writer = new(db);
        for (ulong number = 0; number < levelCount; number++)
        {
            writer.PersistLevel(number, new ChainLevelInfo(true, new BlockInfo(TestItem.KeccakA, number)));
        }

        ChainLevelInfoRepository repository = new(db);
        for (ulong number = 0; number < levelCount; number++)
        {
            repository.LoadLevel(number);
        }

        long readsAfterFirstPass = db.ReadsCount;
        for (ulong number = 0; number < levelCount; number++)
        {
            Assert.That(repository.LoadLevel(number)?.BlockInfos[0].TotalDifficulty, Is.EqualTo((UInt256)number));
        }

        Assert.That(db.ReadsCount, Is.EqualTo(readsAfterFirstPass));
    }

    [Test]
    public void LoadLevel_keeps_a_level_persisted_while_the_load_read_the_db()
    {
        TestMemDb db = new();
        ChainLevelInfo stale = new(false, new BlockInfo(TestItem.KeccakA, 1));
        ChainLevelInfo persisted = new(true, new BlockInfo(TestItem.KeccakB, 2));
        new ChainLevelInfoRepository(db).PersistLevel(1, stale);
        byte[] staleRlp = db.Get(1UL.ToBigEndianByteArrayWithoutLeadingZeros())!;

        ChainLevelInfoRepository repository = new(db);
        db.ReadFunc = _ =>
        {
            db.ReadFunc = null;
            repository.PersistLevel(1, persisted);
            return staleRlp;
        };

        Assert.That(repository.LoadLevel(1), Is.SameAs(persisted));
        Assert.That(repository.LoadLevel(1), Is.SameAs(persisted));
    }

    [Test]
    public void Delete_evicts_the_cached_level()
    {
        MemDb db = new();
        ChainLevelInfoRepository repository = new(db);
        repository.PersistLevel(1, new ChainLevelInfo(true, new BlockInfo(TestItem.KeccakA, 1)));
        Assert.That(repository.LoadLevel(1), Is.Not.Null);

        repository.Delete(1);

        Assert.That(repository.LoadLevel(1), Is.Null);
    }

    [Test]
    public void Delete_clears_a_level_loaded_before_the_db_delete()
    {
        RemoveHookDb db = new();
        ChainLevelInfoRepository repository = new(db);
        repository.PersistLevel(1, new ChainLevelInfo(true, new BlockInfo(TestItem.KeccakA, 1)));
        ((IClearableCache)repository).ClearCache();
        db.BeforeRemove = () =>
        {
            db.BeforeRemove = null;
            Assert.That(repository.LoadLevel(1), Is.Not.Null);
        };

        repository.Delete(1);

        Assert.That(repository.LoadLevel(1), Is.Null);
    }

    [Test]
    public void PersistLevel_in_a_batch_replaces_the_cached_level()
    {
        MemDb db = new();
        ChainLevelInfoRepository repository = new(db);
        repository.PersistLevel(1, new ChainLevelInfo(false, new BlockInfo(TestItem.KeccakA, 1)));
        Assert.That(repository.LoadLevel(1), Is.Not.Null);
        ChainLevelInfo replacement = new(true, new BlockInfo(TestItem.KeccakB, 2));

        using (BatchWrite batch = repository.StartBatch())
        {
            repository.PersistLevel(1, replacement, batch);
            Assert.That(repository.LoadLevel(1), Is.SameAs(replacement));
        }

        AssertChainLevelInfo(new ChainLevelInfoRepository(db).LoadLevel(1), replacement);
    }

    private sealed class RemoveHookDb : TestMemDb
    {
        public Action BeforeRemove { get; set; }

        public override void Remove(ReadOnlySpan<byte> key)
        {
            BeforeRemove?.Invoke();
            base.Remove(key);
        }
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
