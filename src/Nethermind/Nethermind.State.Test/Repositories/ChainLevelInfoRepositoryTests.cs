// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Repositories;
using NUnit.Framework;

namespace Nethermind.Store.Test.Repositories;

public class ChainLevelInfoRepositoryTests
{
    [Test]
    public void TestMultiGet()
    {
        using IContainer container = CreateContainer(new MemDb());
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();

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
    public void MultiLoadLevel_WithFixedLengthKeys_PreservesOwnedResults(
        [Values] bool native, [Values(0, 1, 2, 33, 257)] int count)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        ulong[] requested = new ulong[count];
        ChainLevelInfo[] expected = new ChainLevelInfo[count];
        IOwnedReadOnlyList<ChainLevelInfo> levels;
        using (IDb db = CreateBatchDb(native, directory))
        {
            using IContainer container = CreateContainer(db);
            IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
            for (int i = 0; i < count; i++)
            {
                ulong number = 25_000_000UL + (ulong)(i % 7);
                requested[i] = number;
                if (i % 7 == 1) continue;
                if (i % 7 == 2)
                {
                    db.PutSpan(number.ToBigEndianSpanWithoutLeadingZeros(out _), []);
                    continue;
                }

                expected[i] = CreateBatchLevel(number);
                repository.PersistLevel(number, expected[i]);
            }

            using ArrayPoolListRef<ulong> numbers = new(requested);
            levels = repository.MultiLoadLevel(numbers);
        }

        using (levels)
        {
            Assert.That(levels.Count, Is.EqualTo(count), "the batch must retain the request length");
            for (int i = 0; i < count; i++) AssertBatchLevel(levels[i], expected[i]);
        }
    }

    [Test]
    public void MultiLoadLevel_AtEveryKeyLengthBoundary_PreservesFixedLengthReads(
        [Values] bool native, [ValueSource(nameof(KeyLengthBoundaries))] ulong number)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using IDb db = CreateBatchDb(native, directory);
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        ChainLevelInfo expected = CreateBatchLevel(number);
        repository.PersistLevel(number, expected);

        using ArrayPoolListRef<ulong> numbers = new(2, number, number);
        using IOwnedReadOnlyList<ChainLevelInfo> levels = repository.MultiLoadLevel(numbers);
        Assert.That(levels.Count, Is.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            AssertChainLevelInfo(levels[0], expected);
            AssertChainLevelInfo(levels[1], expected);
        }
    }

    [Test]
    public void MultiLoadLevel_WithMixedLengthKeys_PreservesEncodingAndRequestOrder([Values] bool native)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using IDb db = CreateBatchDb(native, directory);
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        ulong[] requested = [ulong.MaxValue, 0, 256, 255, 65_535, 65_536, 16_777_215, 16_777_216, 256, 0];
        for (int i = 0; i < requested.Length; i++)
        {
            ulong number = requested[i];
            if (number == 256) continue;
            if (number == 65_535)
                db.PutSpan(number.ToBigEndianSpanWithoutLeadingZeros(out _), []);
            else
                repository.PersistLevel(number, CreateBatchLevel(number));
        }

        using ArrayPoolListRef<ulong> numbers = new(requested);
        using IOwnedReadOnlyList<ChainLevelInfo> levels = repository.MultiLoadLevel(numbers);
        Assert.That(levels.Count, Is.EqualTo(requested.Length), "mixed key lengths must not drop requests");
        for (int i = 0; i < requested.Length; i++)
        {
            ulong number = requested[i];
            ChainLevelInfo expected = number is 256 or 65_535 ? null : CreateBatchLevel(number);
            AssertBatchLevel(levels[i], expected);
        }
    }

    [Test]
    public void MultiLoadLevel_WithFixedOrMixedKeyLengths_UsesExpectedBatchPath([Values] bool mixedLengths)
    {
        BatchTrackingDb db = new(mixedLengths);
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        ulong[] requested = mixedLengths ? [255, 256, 255] : [1, 2, 1];
        foreach (ulong number in requested) repository.PersistLevel(number, CreateBatchLevel(number));

        using ArrayPoolListRef<ulong> numbers = new(requested);
        using IOwnedReadOnlyList<ChainLevelInfo> levels = repository.MultiLoadLevel(numbers);
        Assert.That(levels.Count, Is.EqualTo(requested.Length));
        for (int i = 0; i < requested.Length; i++) AssertChainLevelInfo(levels[i], CreateBatchLevel(requested[i]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.DecodedBatchReads, Is.EqualTo(mixedLengths ? 0 : 1));
            Assert.That(db.LegacyBatchReads, Is.EqualTo(mixedLengths ? 1 : 0));
        }
    }

    [Test]
    public void MultiLoadLevel_WhenStoredValueChanges_BypassesSingleLevelCache([Values] bool native)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using IDb db = CreateBatchDb(native, directory);
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        ChainLevelInfo cached = CreateBatchLevel(1);
        ChainLevelInfo replacement = CreateBatchLevel(2);
        repository.PersistLevel(1, cached);
        db.PutSpan(1UL.ToBigEndianSpanWithoutLeadingZeros(out _), Rlp.Encode(replacement).Bytes);
        Assert.That(repository.LoadLevel(1), Is.SameAs(cached), "precondition: the single-key cache still has the old level");

        using ArrayPoolListRef<ulong> numbers = new(1, 1UL);
        using IOwnedReadOnlyList<ChainLevelInfo> levels = repository.MultiLoadLevel(numbers);
        AssertChainLevelInfo(levels[0], replacement);
    }

    [Test]
    public void MultiLoadLevel_WhenDecodeFails_AllowsTheNextBatch(
        [Values] bool native, [Values(2UL, 256UL)] ulong secondNumber)
    {
        using TempPath directory = TempPath.GetTempDirectory();
        using IDb db = CreateBatchDb(native, directory);
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        repository.PersistLevel(1, CreateBatchLevel(1));
        db.PutSpan(secondNumber.ToBigEndianSpanWithoutLeadingZeros(out _), [0x01]);

        Assert.Throws<RlpException>(() =>
        {
            using ArrayPoolListRef<ulong> invalid = new(2, 1UL, secondNumber);
            using IOwnedReadOnlyList<ChainLevelInfo> ignored = repository.MultiLoadLevel(invalid);
        });

        ChainLevelInfo expected = CreateBatchLevel(secondNumber);
        repository.PersistLevel(secondNumber, expected);
        using ArrayPoolListRef<ulong> numbers = new(2, 1UL, secondNumber);
        using IOwnedReadOnlyList<ChainLevelInfo> levels = repository.MultiLoadLevel(numbers);
        using (Assert.EnterMultipleScope())
        {
            AssertChainLevelInfo(levels[0], CreateBatchLevel(1));
            AssertChainLevelInfo(levels[1], expected);
        }
    }

    [Test]
    public void TestClearCache_removes_cached_levels()
    {
        MemDb db = new();
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();

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
        ((IClearableCache)repository).ClearCache();
        Assert.That(repository.LoadLevel(1), Is.Null);
    }

    [Test]
    public void LoadLevel_serves_a_header_cache_sized_window_from_cache()
    {
        const int levelCount = 256 + 16;
        MemDb db = new();
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        for (ulong number = 0; number < levelCount; number++)
        {
            repository.PersistLevel(number, new ChainLevelInfo(true, new BlockInfo(TestItem.KeccakA, number)));
        }

        ((IClearableCache)repository).ClearCache();
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
    public void LoadLevelWithoutCaching_serves_a_cached_level_and_reads_an_uncached_one_through()
    {
        MemDb db = new();
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        ChainLevelInfo level = new(true, new BlockInfo(TestItem.KeccakA, 1));
        PersistUncached(repository, db, level);

        long readsBefore = db.ReadsCount;
        AssertChainLevelInfo(repository.LoadLevelWithoutCaching(1), level);
        AssertChainLevelInfo(repository.LoadLevelWithoutCaching(1), level);
        Assert.That(db.ReadsCount - readsBefore, Is.EqualTo(2));

        ChainLevelInfo cached = repository.LoadLevel(1)!;
        readsBefore = db.ReadsCount;
        Assert.That(repository.LoadLevelWithoutCaching(1), Is.SameAs(cached));
        Assert.That(db.ReadsCount, Is.EqualTo(readsBefore));
    }

    [Test]
    public void LoadLevel_keeps_a_level_persisted_while_the_load_read_the_db([Values] bool withoutCaching)
    {
        TestMemDb db = new();
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        ChainLevelInfo persisted = new(true, new BlockInfo(TestItem.KeccakB, 2));
        byte[] staleRlp = PersistUncached(repository, db, new ChainLevelInfo(false, new BlockInfo(TestItem.KeccakA, 1)));

        db.ReadFunc = _ =>
        {
            db.ReadFunc = null;
            repository.PersistLevel(1, persisted);
            return staleRlp;
        };

        Func<ChainLevelInfo> load = withoutCaching ? () => repository.LoadLevelWithoutCaching(1) : () => repository.LoadLevel(1);
        Assert.That(load(), Is.SameAs(persisted));
        Assert.That(load(), Is.SameAs(persisted));
    }

    [Test]
    public void LoadLevel_does_not_cache_a_level_deleted_while_the_load_read_the_db()
    {
        TestMemDb db = new();
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        byte[] deletedRlp = PersistUncached(repository, db, new ChainLevelInfo(true, new BlockInfo(TestItem.KeccakA, 1)));

        db.ReadFunc = _ =>
        {
            db.ReadFunc = null;
            repository.Delete(1);
            return deletedRlp;
        };

        Assert.That(repository.LoadLevel(1), Is.Not.Null);
        Assert.That(repository.LoadLevel(1), Is.Null);
    }

    [Test]
    public void Delete_clears_a_level_loaded_before_the_db_delete()
    {
        RemoveHookDb db = new();
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        PersistUncached(repository, db, new ChainLevelInfo(true, new BlockInfo(TestItem.KeccakA, 1)));
        db.BeforeRemove = () =>
        {
            db.BeforeRemove = null;
            Assert.That(repository.LoadLevel(1), Is.Not.Null);
        };

        repository.Delete(1);

        Assert.That(repository.LoadLevel(1), Is.Null);
    }

    [Test]
    public void Delete_evicts_the_cached_level()
    {
        using IContainer container = CreateContainer(new MemDb());
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        repository.PersistLevel(1, new ChainLevelInfo(true, new BlockInfo(TestItem.KeccakA, 1)));
        Assert.That(repository.LoadLevel(1), Is.Not.Null);

        repository.Delete(1);

        Assert.That(repository.LoadLevel(1), Is.Null);
    }

    [Test]
    public void PersistLevel_in_a_batch_replaces_the_cached_level()
    {
        using IContainer container = CreateContainer(new MemDb());
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        repository.PersistLevel(1, new ChainLevelInfo(false, new BlockInfo(TestItem.KeccakA, 1)));
        Assert.That(repository.LoadLevel(1), Is.Not.Null);
        ChainLevelInfo replacement = new(true, new BlockInfo(TestItem.KeccakB, 2));

        using (BatchWrite batch = repository.StartBatch())
        {
            repository.PersistLevel(1, replacement, batch);
            Assert.That(repository.LoadLevel(1), Is.SameAs(replacement));
        }

        ((IClearableCache)repository).ClearCache();
        AssertChainLevelInfo(repository.LoadLevel(1), replacement);
    }

    private static IContainer CreateContainer(IDb blockInfosDb) => new ContainerBuilder()
        .AddModule(new TestNethermindModule())
        .AddKeyedSingleton<IDb>(DbNames.BlockInfos, blockInfosDb)
        .Build();

    private static IDb CreateBatchDb(bool native, TempPath directory)
    {
        if (!native) return new MemDb();
        using IContainer configs = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
        return new DbOnTheRocks(directory.Path, new DbSettings(DbNames.BlockInfos, "levels"),
            configs.Resolve<IDbConfig>(), configs.Resolve<IRocksDbConfigFactory>(), LimboLogs.Instance);
    }

    private static IEnumerable<ulong> KeyLengthBoundaries()
    {
        yield return 0;
        for (int bytes = 1; bytes < sizeof(ulong); bytes++)
        {
            ulong boundary = 1UL << (bytes * 8);
            yield return boundary - 1;
            yield return boundary;
        }
        yield return ulong.MaxValue;
    }

    private static ChainLevelInfo CreateBatchLevel(ulong number) =>
        new(true, new BlockInfo(TestItem.KeccakA, number), new BlockInfo(TestItem.KeccakB, UInt256.One));

    private static void AssertBatchLevel(ChainLevelInfo actual, ChainLevelInfo expected)
    {
        if (expected is null)
            Assert.That(actual, Is.Null, "missing and empty database values must remain null");
        else
            AssertChainLevelInfo(actual, expected);
    }

    /// <summary>Persists <paramref name="level"/> at number 1 and leaves the cache cold, returning its stored RLP.</summary>
    private static byte[] PersistUncached(IChainLevelInfoRepository repository, IDb db, ChainLevelInfo level)
    {
        repository.PersistLevel(1, level);
        ((IClearableCache)repository).ClearCache();
        return db.Get(1UL.ToBigEndianByteArrayWithoutLeadingZeros())!;
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

    private sealed class BatchTrackingDb(bool mixedLengths) : MemDb, IDb, IBatchReadOnlyDb
    {
        public int DecodedBatchReads { get; private set; }
        public int LegacyBatchReads { get; private set; }

        KeyValuePair<byte[], byte[]>[] IDb.this[byte[][] keys]
        {
            get
            {
                if (!mixedLengths) throw new InvalidOperationException("Fixed-width keys must use decoded batch reads.");
                LegacyBatchReads++;
                return base[keys];
            }
        }

        void IBatchReadOnlyDb.GetMany<T, TDecoder>(ReadOnlySpan<byte> keys, int keyLength, Span<T> values, TDecoder decoder)
        {
            if (mixedLengths) throw new InvalidOperationException("Mixed-width keys must use one legacy batch.");
            DecodedBatchReads++;
            for (int i = 0; i < values.Length; i++)
            {
                byte[] value = Get(keys.Slice(i * keyLength, keyLength));
                values[i] = value is null ? default! : decoder.Decode(value);
            }
        }
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
}
