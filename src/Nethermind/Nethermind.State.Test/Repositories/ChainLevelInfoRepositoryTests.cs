// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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
        Assert.That(db.ReadsCount - readsBefore, Is.EqualTo(1));

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
        EvictDecodedLevels(repository);
        AssertChainLevelInfo(load(), persisted);
    }

    [Test]
    public void LoadLevel_does_not_cache_a_level_deleted_while_the_load_read_the_db([Values] bool withoutCaching)
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

        Assert.That(LoadLevel(repository, withoutCaching), Is.Not.Null);
        Assert.That(LoadLevel(repository, withoutCaching), Is.Null);
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

    [Test]
    public void Encoded_levels_are_reused_without_sharing_mutable_results([Values] bool withoutCaching, [Values] bool native)
    {
        TestMemDb db = native ? new NativeTestMemDb() : new TestMemDb();
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        ChainLevelInfo expected = CreateBatchLevel(1);
        byte[] encoded = PersistUncached(repository, db, expected);
        long reads = db.ReadsCount;
        ChainLevelInfo first = LoadLevel(repository, withoutCaching)!;
        if (withoutCaching) Assert.That(CacheCount(repository, "_blockInfoCache"), Is.Zero);
        EvictDecodedLevels(repository);
        first.HasBlockOnMainChain = false;
        first.BlockInfos[0].TotalDifficulty = 999;
        first.BlockInfos[0].BlockHash.Bytes[0] ^= 1;
        first.BlockInfos = [];
        Assert.That(encoded, Is.EqualTo(Rlp.Encode(expected).Bytes), "returned objects must not alias the database input");
        Array.Fill(encoded, (byte)0);

        ChainLevelInfo second = LoadLevel(repository, withoutCaching)!;

        AssertChainLevelInfo(second, expected);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(db.ReadsCount - reads, Is.EqualTo(1));
            if (withoutCaching) Assert.That(CacheCount(repository, "_blockInfoCache"), Is.Zero);
            else Assert.That(repository.LoadLevelWithoutCaching(1), Is.SameAs(second));
        }
    }

    [Test]
    public void Encoded_levels_are_invalidated_by_persist_or_delete([Values] bool delete, [Values] bool batched)
    {
        TestMemDb db = new();
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        PersistUncached(repository, db, CreateBatchLevel(1));
        Assert.That(repository.LoadLevelWithoutCaching(1), Is.Not.Null);
        ChainLevelInfo replacement = CreateBatchLevel(2);
        using (BatchWrite batch = batched ? repository.StartBatch() : null)
        {
            if (delete) repository.Delete(1, batch);
            else repository.PersistLevel(1, replacement, batch);
        }
        EvictDecodedLevels(repository);

        AssertBatchLevel(repository.LoadLevelWithoutCaching(1), delete ? null : replacement);
    }

    [Test]
    public void ClearCache_invalidates_encoded_levels()
    {
        TestMemDb db = new();
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        PersistUncached(repository, db, CreateBatchLevel(1));
        Assert.That(repository.LoadLevelWithoutCaching(1), Is.Not.Null);
        db.Clear();

        ((IClearableCache)repository).ClearCache();

        Assert.That(repository.LoadLevelWithoutCaching(1), Is.Null);
    }

    [Test]
    public void Encoded_levels_do_not_refill_after_decoder_mutation([Values] bool clear, [Values] bool warm, [Values] bool withoutCaching)
    {
        TestMemDb db = new();
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        PersistUncached(repository, db, CreateBatchLevel(1));
        if (warm) Assert.That(repository.LoadLevelWithoutCaching(1), Is.Not.Null);
        bool mutated = false;
        MutatingLevelDecoder decoder = new(() =>
        {
            if (mutated) return;
            mutated = true;
            if (clear)
            {
                db.Clear();
                ((IClearableCache)repository).ClearCache();
            }
            else repository.Delete(1);
        });
        typeof(ChainLevelInfoRepository).GetField("_decoder", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(repository, decoder);

        Assert.That(LoadLevel(repository, withoutCaching), Is.Not.Null);
        EvictDecodedLevels(repository);

        Assert.That(LoadLevel(repository, withoutCaching), Is.Null);
        Assert.That(decoder.LastBehaviors, Is.EqualTo(RlpBehaviors.AllowExtraBytes));
    }

    [Test]
    public void Active_batch_reads_cannot_publish_precommit_rows(
        [Values] bool otherThread, [Values] bool flush, [Values] bool delete, [Values] bool withoutCaching)
    {
        TestMemDb db = new();
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        ChainLevelInfo original = CreateBatchLevel(1);
        ChainLevelInfo replacement = CreateBatchLevel(2);
        PersistUncached(repository, db, original);
        AssertChainLevelInfo(repository.LoadLevelWithoutCaching(1), original);
        using BatchWrite batch = repository.StartBatch();
        if (delete) repository.Delete(1, batch);
        else repository.PersistLevel(1, replacement, batch);
        EvictDecodedLevels(repository);
        long reads = db.ReadsCount;

        AssertChainLevelInfo(ReadWhileBatchOwnsLock(repository, batch, withoutCaching, otherThread), original);
        Assert.That(db.ReadsCount - reads, Is.EqualTo(1), "an active batch must not admit an encoded-cache hit");
        EvictDecodedLevels(repository);
        if (flush)
        {
            batch.Flush();
            AssertBatchLevel(ReadWhileBatchOwnsLock(repository, batch, withoutCaching, otherThread), delete ? null : replacement);
            EvictDecodedLevels(repository);
        }
        batch.Dispose();

        AssertBatchLevel(LoadLevel(repository, withoutCaching), delete ? null : replacement);
    }

    [Test]
    public void Active_batch_bypasses_preexisting_encoded_hits([Values] bool otherThread)
    {
        TestMemDb db = new();
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        ChainLevelInfo original = CreateBatchLevel(1);
        PersistUncached(repository, db, original);
        AssertChainLevelInfo(repository.LoadLevelWithoutCaching(1), original);
        using BatchWrite batch = repository.StartBatch();
        repository.PersistLevel(2, CreateBatchLevel(2), batch);
        long reads = db.ReadsCount;

        AssertChainLevelInfo(ReadWhileBatchOwnsLock(repository, batch, withoutCaching: true, otherThread), original);
        Assert.That(db.ReadsCount - reads, Is.EqualTo(1));
        batch.Dispose();
        reads = db.ReadsCount;
        AssertChainLevelInfo(repository.LoadLevelWithoutCaching(1), original);
        Assert.That(db.ReadsCount, Is.EqualTo(reads));
    }

    [Test]
    public void A_writer_started_during_a_read_prevents_publication_without_blocking([Values] bool withoutCaching)
    {
        TestMemDb db = new();
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        ChainLevelInfo original = CreateBatchLevel(1);
        ChainLevelInfo replacement = CreateBatchLevel(2);
        byte[] stale = PersistUncached(repository, db, original);
        using ManualResetEventSlim reading = new();
        using ManualResetEventSlim resume = new();
        db.ReadFunc = _ =>
        {
            db.ReadFunc = null;
            reading.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Reader was not resumed.");
            return stale;
        };
        Task<ChainLevelInfo> read = Task.Run(() => LoadLevel(repository, withoutCaching));
        try
        {
            Assert.That(reading.Wait(TimeSpan.FromSeconds(5)), Is.True);
            using BatchWrite batch = repository.StartBatch();
            repository.PersistLevel(1, replacement, batch);
            EvictDecodedLevels(repository);
            resume.Set();

            AssertChainLevelInfo(read.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(), original);
            batch.Dispose();
            EvictDecodedLevels(repository);
            AssertChainLevelInfo(LoadLevel(repository, withoutCaching), replacement);
        }
        finally
        {
            resume.Set();
            read.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }

    [Test]
    public void Encoded_level_cache_has_bounded_entries()
    {
        TestMemDb db = new();
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        byte[] encoded = Rlp.Encode(CreateBatchLevel(1)).Bytes;
        db.ReadFunc = _ => encoded;

        for (ulong number = 1; number <= 16384; number++) repository.LoadLevelWithoutCaching(number);

        Assert.That(CacheCount(repository, "_encodedLevelCache"), Is.InRange(1, 8192));
        ((IClearableCache)repository).ClearCache();
        Assert.That(CacheCount(repository, "_encodedLevelCache"), Is.Zero);
    }

    [Test]
    public void Oversize_encoded_levels_are_not_retained()
    {
        TestMemDb db = new();
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        BlockInfo[] blocks = new BlockInfo[40];
        for (int i = 0; i < blocks.Length; i++) blocks[i] = new BlockInfo(TestItem.KeccakA, (UInt256)i);
        ChainLevelInfo large = new(true, blocks);
        byte[] encoded = PersistUncached(repository, db, large);
        Assert.That(encoded.Length, Is.GreaterThan(1024));
        long reads = db.ReadsCount;

        AssertChainLevelInfo(repository.LoadLevelWithoutCaching(1), large);
        AssertChainLevelInfo(repository.LoadLevelWithoutCaching(1), large);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.ReadsCount - reads, Is.EqualTo(2));
            Assert.That(CacheCount(repository, "_encodedLevelCache"), Is.Zero);
        }
    }

    [Test]
    public void Encoded_levels_do_not_cache_missing_or_empty_rows([Values] bool empty)
    {
        TestMemDb db = new();
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        ReadOnlySpan<byte> key = 1UL.ToBigEndianSpanWithoutLeadingZeros(out _);
        if (empty) db.Set(key, []);
        Assert.That(repository.LoadLevelWithoutCaching(1), Is.Null);
        ChainLevelInfo expected = CreateBatchLevel(1);
        db.Set(key, Rlp.Encode(expected).Bytes);

        AssertChainLevelInfo(repository.LoadLevelWithoutCaching(1), expected);
    }

    [Test]
    public void Encoded_levels_release_borrowed_memory_and_own_their_snapshots([Values] bool native, [Values] bool malformed)
    {
        BorrowedLevelDb db = native ? new NativeBorrowedLevelDb() : new BorrowedLevelDb();
        using IContainer container = CreateContainer(db);
        IChainLevelInfoRepository repository = container.Resolve<IChainLevelInfoRepository>();
        ChainLevelInfo expected = CreateBatchLevel(1);
        byte[] encoded = PersistUncached(repository, db, expected);
        if (malformed)
        {
            db.Set(1UL.ToBigEndianSpanWithoutLeadingZeros(out _), [0x01]);
            Assert.Throws<RlpException>(() => repository.LoadLevelWithoutCaching(1));
            Assert.That(db.Releases, Is.EqualTo(1));
            db.Set(1UL.ToBigEndianSpanWithoutLeadingZeros(out _), encoded);
        }

        AssertChainLevelInfo(repository.LoadLevelWithoutCaching(1), expected);
        AssertChainLevelInfo(repository.LoadLevelWithoutCaching(1), expected);

        Assert.That(db.Releases, Is.EqualTo(malformed ? 2 : 1));
    }

    private static ChainLevelInfo LoadLevel(IChainLevelInfoRepository repository, bool withoutCaching) =>
        withoutCaching ? repository.LoadLevelWithoutCaching(1) : repository.LoadLevel(1);

    private static ChainLevelInfo ReadWhileBatchOwnsLock(IChainLevelInfoRepository repository, BatchWrite batch, bool withoutCaching, bool otherThread)
    {
        if (!otherThread) return LoadLevel(repository, withoutCaching);
        Task<ChainLevelInfo> read = Task.Run(() => LoadLevel(repository, withoutCaching));
        try
        {
            return read.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
        catch
        {
            batch.Dispose();
            read.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            throw;
        }
    }

    private static object Cache(IChainLevelInfoRepository repository, string name) =>
        typeof(ChainLevelInfoRepository).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(repository)!;

    private static void EvictDecodedLevels(IChainLevelInfoRepository repository)
    {
        object cache = Cache(repository, "_blockInfoCache");
        cache.GetType().GetMethod("Clear")!.Invoke(cache, [true]);
    }

    private static int CacheCount(IChainLevelInfoRepository repository, string name)
    {
        object cache = Cache(repository, name);
        return (int)cache.GetType().GetProperty("Count")!.GetValue(cache)!;
    }

    private class BorrowedLevelDb : TestMemDb, IReadOnlyKeyValueStore
    {
        private byte[] _borrowed;
        public int Releases { get; private set; }

        public Span<byte> Borrow(scoped ReadOnlySpan<byte> key, ReadFlags flags)
        {
            byte[] bytes = Get(key, flags);
            _borrowed = bytes is null ? null : (byte[])bytes.Clone();
            return _borrowed;
        }

        public void Release()
        {
            Releases++;
            _borrowed.AsSpan().Fill(0);
            _borrowed = null;
        }

        Span<byte> IReadOnlyKeyValueStore.GetSpan(scoped ReadOnlySpan<byte> key, ReadFlags flags) => Borrow(key, flags);
        void IReadOnlyKeyValueStore.DangerousReleaseMemory(in ReadOnlySpan<byte> span) => Release();
    }

    private sealed class NativeBorrowedLevelDb : BorrowedLevelDb, IReadOnlyNativeKeyValueStore
    {
        public ReadOnlySpan<byte> GetNativeSlice(scoped ReadOnlySpan<byte> key, out nint handle, ReadFlags flags)
        {
            handle = 0;
            return Borrow(key, flags);
        }
        public void DangerousReleaseHandle(nint handle) => Release();
    }

    private sealed class MutatingLevelDecoder(Action afterDecode) : RlpDecoder<ChainLevelInfo>
    {
        private readonly ChainLevelDecoder _inner = new();
        public RlpBehaviors LastBehaviors { get; private set; }

        public override int GetLength(ChainLevelInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) => _inner.GetLength(item, rlpBehaviors);
        public override void Encode<TWriter>(ref TWriter writer, ChainLevelInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None) =>
            _inner.Encode(ref writer, item, rlpBehaviors);

        protected override ChainLevelInfo DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            LastBehaviors = rlpBehaviors;
            ChainLevelInfo level = _inner.Decode(ref decoderContext, rlpBehaviors);
            afterDecode();
            return level;
        }
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
