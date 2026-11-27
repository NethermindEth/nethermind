// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;
using Testably.Abstractions;

namespace Nethermind.State.Flat.Test.Persistence;

public class TrieNodeLogTests
{
    private static readonly TreePath TopPath = TreePath.FromHexString("12345"); // StateTopNodes
    private static readonly TreePath MediumPath = TreePath.FromHexString("123456789abc"); // StateNodes
    private static readonly TreePath StoragePath = TreePath.FromHexString("abcd"); // StorageNodes
    private static readonly byte[] Rlp1 = Bytes.FromHexString("0x1111111111");
    private static readonly byte[] Rlp2 = Bytes.FromHexString("0x2222222222");
    private static readonly byte[] Rlp3 = Bytes.FromHexString("0x3333333333");
    private static readonly IFileSystem FileSystem = new RealFileSystem();

    private TempPath _directory = null!;
    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private FlatDbConfig _config = null!;
    private TrieNodeLog _log = null!;
    private RocksDbPersistence _persistence = null!;

    [SetUp]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        // Two shards per partition, so every shard gets a 4 KiB generation; otherwise the shipped defaults.
        _config = new FlatDbConfig { TrieNodeLogEnabled = true, TrieNodeLogStateBytes = 8192, TrieNodeLogStorageBytes = 8192 };
        Open();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _log.DisposeAsync();
        _db.Dispose();
        _directory.Dispose();
    }

    private void Open()
    {
        _log = new TrieNodeLog(_directory.Path, FileSystem, _db, _config, LimboLogs.Instance);
        _persistence = new RocksDbPersistence(_db, LimboLogs.Instance, _log);
    }

    private async Task Reopen()
    {
        await _log.DisposeAsync();
        Open();
    }

    private static StateId State(ulong number) => number == 0 ? StateId.PreGenesis : new StateId(number, ValueKeccak.Compute($"state{number}"));

    private IPersistence.IWriteBatch Batch(ulong from, ulong to) => _persistence.CreateWriteBatch(State(from), State(to), WriteFlags.None);

    private void WriteTop(ulong from, ulong to, byte[] rlp)
    {
        using IPersistence.IWriteBatch batch = Batch(from, to);
        batch.SetStateTrieNode(TopPath, rlp);
    }

    private byte[]? ReadTop()
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        return reader.LoadStateRlp(TopPath, ReadFlags.None);
    }

    /// <summary>Reads the trie columns directly, bypassing the log.</summary>
    private BaseTriePersistence.Reader Raw() => new(
        _db.GetColumnDb(FlatDbColumns.StateTopNodes),
        _db.GetColumnDb(FlatDbColumns.StateNodes),
        _db.GetColumnDb(FlatDbColumns.StorageNodes),
        _db.GetColumnDb(FlatDbColumns.FallbackNodes));

    private string[] LogFiles() => Directory.GetFiles(_directory.Path, "*.log", SearchOption.AllDirectories);

    /// <summary>Random bytes of the given length.</summary>
    private static byte[] Value(byte seed, int length)
    {
        byte[] value = new byte[length];
        new Random(seed).NextBytes(value);
        return value;
    }

    private static long FlushedStateBytes() =>
        Metrics.TrieNodeLogFlushedBytes.TryGetValue(TrieNodeLogLabel.State, out long bytes) ? bytes : 0;

    private static long SecondLevelStoredStateBytes() =>
        Metrics.TrieNodeLogSecondLevelStoredBytes.TryGetValue(TrieNodeLogLabel.State, out long bytes) ? bytes : 0;

    [Test]
    public void TrimFlushedGenerations_WithFlushedPrefix_ReleasesOnlyThatPrefix(
        [Values(0, 1, 7)] int flushedCount, [Values(0, 1, 4)] int retainedCount)
    {
        using ArrayPoolList<TrieNodeLogGeneration> pinned = new(flushedCount + retainedCount);
        List<TrieNodeLogGeneration> generations = [];
        try
        {
            for (int i = 0; i < flushedCount + retainedCount; i++)
            {
                TrieNodeLogGeneration generation = CreateTrimGeneration(i);
                generations.Add(generation);
                pinned.Add(generation);
            }

            Assert.That(pinned.Count, Is.EqualTo(flushedCount + retainedCount), "precondition: the list owns every generation");

            TrieNodeLogView.TrimFlushedGenerations(pinned, (ulong)flushedCount);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pinned.Count, Is.EqualTo(retainedCount), "only generations after the flushed marker remain pinned");
                for (int i = 0; i < generations.Count; i++)
                {
                    Assert.That(generations[i].Handle.IsClosed, Is.EqualTo(i < flushedCount), "the removed prefix releases its files while the suffix stays live");
                }
                for (int i = 0; i < retainedCount; i++)
                {
                    Assert.That(pinned[i], Is.SameAs(generations[flushedCount + i]), "survivors retain their original order and leases");
                }
            }
        }
        finally
        {
            foreach (TrieNodeLogGeneration generation in generations)
            {
                if (!generation.Handle.IsClosed) generation.Dispose();
            }
        }
    }

    [Test]
    public void TrimFlushedGenerations_WhenCleanupFails_DropsReleasedEntriesAndRetainsUntouchedLeases()
    {
        IFile file = Substitute.For<IFile>();
        IFileSystem failingFileSystem = Substitute.For<IFileSystem>();
        failingFileSystem.File.Returns(file);
        file.OpenHandle(Arg.Any<string>(), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite)
            .Returns(call => FileSystem.File.OpenHandle(call.ArgAt<string>(0), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite));
        file.When(f => f.Delete(Arg.Any<string>())).Do(static _ => throw new IOException("delete failed"));

        using ArrayPoolList<TrieNodeLogGeneration> pinned = new(4);
        List<TrieNodeLogGeneration> generations = [];
        try
        {
            for (int i = 0; i < 4; i++)
            {
                TrieNodeLogGeneration generation = CreateTrimGeneration(i, i == 1 ? failingFileSystem : FileSystem);
                generations.Add(generation);
                pinned.Add(generation);
            }
            Assert.That(pinned.Count, Is.EqualTo(4), "precondition: three flushed generations precede a retained generation");

            Assert.That(() => TrieNodeLogView.TrimFlushedGenerations(pinned, 3),
                Throws.TypeOf<IOException>().With.Message.EqualTo("delete failed"));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(pinned.Count, Is.EqualTo(2), "both released entries must leave the list even when the second cleanup throws");
                Assert.That(generations[0].Handle.IsClosed, Is.True, "the first generation was released normally");
                Assert.That(generations[1].Handle.IsClosed, Is.True, "the failing cleanup already released its file handle");
                Assert.That(pinned[0], Is.SameAs(generations[2]), "the unvisited flushed generation keeps its lease and position");
                Assert.That(pinned[1], Is.SameAs(generations[3]), "the retained generation stays after the unvisited prefix");
            }

            TrieNodeLogView.TrimFlushedGenerations(pinned, 3);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(generations[2].Handle.IsClosed, Is.True, "retry releases only the remaining flushed entry");
                Assert.That(pinned.Count, Is.EqualTo(1), "released entries must not be disposed a second time");
                Assert.That(pinned[0], Is.SameAs(generations[3]), "retry preserves the retained generation");
            }
        }
        finally
        {
            foreach (TrieNodeLogGeneration generation in generations)
            {
                if (!generation.Handle.IsClosed) generation.Dispose();
            }
        }
    }

    // Propagate deletion failure to exercise the same lease invariant as a Handle.Dispose failure.
    private TrieNodeLogGeneration CreateTrimGeneration(int index, IFileSystem? fileSystem = null) =>
        new((ulong)index + 1, Path.Combine(_directory.Path, $"trim-{index}.log"), fileSystem ?? FileSystem, 1024,
            static (_, exception) => throw exception);

    [Test]
    public void Readers_see_the_version_of_their_snapshot_across_overwrites_and_merges()
    {
        using IPersistence.IPersistenceReader before = _persistence.CreateReader();
        WriteTop(0, 1, Rlp1);
        using IPersistence.IPersistenceReader atV1 = _persistence.CreateReader();
        WriteTop(1, 2, Rlp2);
        using IPersistence.IPersistenceReader atV2 = _persistence.CreateReader();

        _log.Drain();
        using IPersistence.IPersistenceReader afterMerge = _persistence.CreateReader();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(before.LoadStateRlp(TopPath, ReadFlags.None), Is.Null);
            Assert.That(atV1.LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(atV2.LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp2));
            Assert.That(afterMerge.LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp2));
            Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp2));
            Assert.That(LogFiles(), Is.Not.Empty, "the merged generation stays on disk while a reader pins it");
        }

        atV1.Dispose();
        atV2.Dispose();
        Assert.That(LogFiles, Is.Empty.After(5000, 20));
    }

    [Test]
    public void Merge_writes_only_the_latest_record_per_key_and_applies_tombstones()
    {
        // The node must be in RocksDB for the range delete's scan to find it: sync batches bypass the log.
        using (IPersistence.IWriteBatch sync = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            sync.SetStateTrieNode(MediumPath, Rlp1);
        }

        WriteTop(0, 1, Rlp1);
        using (IPersistence.IWriteBatch batch = Batch(1, 2))
        {
            batch.SetStateTrieNode(TopPath, Rlp2);
            batch.SetStateTrieNode(TopPath, Rlp3);
            batch.DeleteStateTrieNodeRange(new ValueHash256("0x1000000000000000000000000000000000000000000000000000000000000000"), new ValueHash256("0x2000000000000000000000000000000000000000000000000000000000000000"));
        }

        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp3));
            Assert.That(reader.LoadStateRlp(MediumPath, ReadFlags.None), Is.Null, "tombstone hides the RocksDB value");
            Assert.That(Raw().LoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp1));
        }

        long flushedBefore = FlushedStateBytes();
        _log.Drain();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp3));
            Assert.That(Raw().LoadStateRlp(MediumPath, ReadFlags.None), Is.Null);
            Assert.That(FlushedStateBytes() - flushedBefore, Is.EqualTo(3 + Rlp3.Length + 8), "one 3-byte top key with its latest value and one 8-byte tombstone key");
        }
    }

    [Test]
    public void A_null_span_deletes_and_an_empty_value_is_stored()
    {
        WriteTop(0, 1, Rlp1);
        using (IPersistence.IWriteBatch batch = Batch(1, 2))
        {
            batch.SetStateTrieNode(TopPath, default);
            batch.SetStateTrieNode(MediumPath, Array.Empty<byte>());
        }

        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.LoadStateRlp(TopPath, ReadFlags.None), Is.Null);
            Assert.That(reader.LoadStateRlp(MediumPath, ReadFlags.None), Is.Empty);
        }

        _log.Drain();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.Null);
            Assert.That(Raw().LoadStateRlp(MediumPath, ReadFlags.None), Is.Empty);
        }
    }

    [Test]
    public void Three_columns_are_logged_and_fallback_goes_direct()
    {
        Hash256 address = TestItem.KeccakA;
        TreePath longPath = TreePath.FromHexString("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"); // FallbackNodes
        using (IPersistence.IWriteBatch batch = Batch(0, 1))
        {
            batch.SetStateTrieNode(TopPath, Rlp1);
            batch.SetStateTrieNode(MediumPath, Rlp2);
            batch.SetStorageTrieNode(address, StoragePath, Rlp3);
            batch.SetStateTrieNode(longPath, Rlp1);
        }

        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.LoadStateRlp(longPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(Raw().LoadStateRlp(longPath, ReadFlags.None), Is.EqualTo(Rlp1), "fallback nodes bypass the log");
            Assert.That(reader.LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(reader.LoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp2));
            Assert.That(reader.LoadStorageRlp(address, StoragePath, ReadFlags.None), Is.EqualTo(Rlp3));
            Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.Null);
            Assert.That(Raw().LoadStateRlp(MediumPath, ReadFlags.None), Is.Null);
            Assert.That(Raw().LoadStorageRlp(address, StoragePath, ReadFlags.None), Is.Null);
        }

        _log.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(Raw().LoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp2));
            Assert.That(Raw().LoadStorageRlp(address, StoragePath, ReadFlags.None), Is.EqualTo(Rlp3));
        }
    }

    [Test]
    public void Sync_batch_drains_the_log_and_writes_directly()
    {
        WriteTop(0, 1, Rlp1);
        Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.Null);

        using (IPersistence.IWriteBatch sync = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            sync.SetStateTrieNode(MediumPath, Rlp2);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(Raw().LoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp2));
            Assert.That(ReadTop(), Is.EqualTo(Rlp1));
        }
        Assert.That(LogFiles, Is.Empty.After(5000, 20));
    }

    [Test]
    public async Task Restart_keeps_committed_records_and_drops_the_rest()
    {
        // Values larger than a generation's byte budget force a roll inside the batch, so the batch spans two files.
        byte[] large = Value(7, 5000);
        WriteTop(0, 1, Rlp1);
        using (IPersistence.IWriteBatch batch = Batch(1, 2))
        {
            batch.SetStateTrieNode(TopPath, large);
            batch.SetStateTrieNode(MediumPath, Rlp2);
        }
        WriteTop(2, 3, Rlp3);

        // A torn tail plus a batch whose RocksDB write never happened: roll the confirmed version back by one.
        await _log.DisposeAsync();
        foreach (string file in LogFiles()) File.AppendAllText(file, "torn tail garbage");
        foreach (TrieNodeLogShard shard in _log.Shards)
        {
            byte[] version = _db.GetColumnDb(FlatDbColumns.Metadata).Get(shard.VersionKey)!;
            version[^1]--;
            _db.GetColumnDb(FlatDbColumns.Metadata).Set(shard.VersionKey, version);
        }
        Open();

        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(large));
            Assert.That(reader.LoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp2));
        }
    }

    [Test]
    public async Task Restart_after_everything_was_merged_continues_above_the_marker()
    {
        WriteTop(0, 1, Rlp1);
        _log.Drain();
        Assert.That(LogFiles, Is.Empty.After(5000, 20));

        await Reopen();
        WriteTop(1, 2, Rlp2);
        Assert.That(ReadTop(), Is.EqualTo(Rlp2));

        await Reopen();
        Assert.That(ReadTop(), Is.EqualTo(Rlp2));
    }

    [Test]
    public async Task Wiped_db_drops_the_log_on_restart()
    {
        WriteTop(0, 1, Rlp1);
        await _log.DisposeAsync();
        BasePersistence.ClearAllColumns(_db);
        Open();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(LogFiles(), Is.Empty);
            Assert.That(ReadTop(), Is.Null);
        }
    }

    [Test]
    public void Merge_lag_skips_keys_rewritten_in_newer_generations()
    {
        // First level only, so the merges under test reach RocksDB directly.
        _config.TrieNodeLogMergeLag = 1;
        _config.TrieNodeLogSecondLevelMergeLag = -1;
        Reopen().GetAwaiter().GetResult();

        // 3000-byte values: two per 4 KiB generation, so every second batch seals one.
        static byte[] Value(byte seed) => TrieNodeLogTests.Value(seed, 3000);

        TreePath coldPath = TreePath.FromHexString("1234"); // same partition and shard as TopPath, written once
        using (IPersistence.IWriteBatch batch = Batch(0, 1))
        {
            batch.SetStateTrieNode(TopPath, Value(1));
            batch.SetStateTrieNode(coldPath, Rlp1);
        }
        WriteTop(1, 2, Value(2)); // seals generation 1
        WriteTop(2, 3, Value(3)); // generation 2

        Assert.That(Raw().LoadStateRlp(coldPath, ReadFlags.None), Is.Null, "a lag of one keeps generation 1 unmerged until generation 2 is sealed");

        WriteTop(3, 4, Value(4)); // seals generation 2, so generation 1 is merged
        Assert.That(() => Raw().LoadStateRlp(coldPath, ReadFlags.None), Is.EqualTo(Rlp1).After(5000, 20));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.Null, "rewritten in generation 2, so not merged from generation 1");
            Assert.That(ReadTop(), Is.EqualTo(Value(4)));
        }
        Assert.That(LogFiles, Has.Length.EqualTo(1).After(5000, 20), "generation 1 deleted, generation 2 sealed and waiting for a newer one");

        _log.Drain();
        Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Value(4)));
    }

    private async Task ReopenWithSecondLevel(int secondLevelMergeLag)
    {
        _config.TrieNodeLogMergeLag = 0;
        _config.TrieNodeLogSecondLevelMergeLag = secondLevelMergeLag;
        await Reopen();
    }

    private string[] ShardFiles(string shard) => Directory.GetFiles(Path.Combine(_directory.Path, shard));

    private static TreePath DistinctPath(ulong block) => TreePath.FromHexString($"1234{block}"); // StateTopNodes, first state shard

    /// <summary>Writes one node per block that fills a generation, each under its own key, so no merge or copy skips one.</summary>
    private void WriteFullGenerations(ulong count)
    {
        for (ulong block = 0; block < count; block++)
        {
            using IPersistence.IWriteBatch batch = Batch(block, block + 1);
            batch.SetStateTrieNode(DistinctPath(block), Value((byte)block, 4096));
        }
    }

    private byte[]? ReadNode(ulong block)
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        return reader.LoadStateRlp(DistinctPath(block), ReadFlags.None);
    }

    /// <summary>
    /// Reopens the log with second-level merges failing at the trie columns' flush, which first-level copies never
    /// flush, and fills the second level's backlog until the third first-level copy waits on it.
    /// </summary>
    private async Task SaturateSecondLevel()
    {
        await _log.DisposeAsync();
        _config.TrieNodeLogMergeLag = 0;
        _config.TrieNodeLogSecondLevelMergeLag = 0;
        _log = new TrieNodeLog(_directory.Path, FileSystem, WithUnflushable(static column => column != FlatDbColumns.Metadata), _config, LimboLogs.Instance);
        _persistence = new RocksDbPersistence(_db, LimboLogs.Instance, _log);
        WriteFullGenerations(3);
        Assert.That(() => ShardFiles("state-0-l2"), Has.Length.EqualTo(2).After(5000, 20));
        Assert.That(() => ShardFiles("state-0"), Has.Length.EqualTo(1).After(5000, 20), "the third copy is waiting");
    }

    [Test]
    public async Task Second_level_takes_merged_generations_and_merges_its_own_full_ones_into_RocksDB()
    {
        await ReopenWithSecondLevel(secondLevelMergeLag: 0);

        // 3000-byte values: two per 4 KiB generation, so every second batch seals one.
        static byte[] Value(byte seed) => TrieNodeLogTests.Value(seed, 3000);

        TreePath coldPath = TreePath.FromHexString("1234"); // same shard as TopPath, written once
        using (IPersistence.IWriteBatch batch = Batch(0, 1))
        {
            batch.SetStateTrieNode(TopPath, Value(1));
            batch.SetStateTrieNode(coldPath, Rlp1);
        }
        long secondLevelStoredBefore = SecondLevelStoredStateBytes();
        WriteTop(1, 2, Value(2)); // seals first-level generation 1, copied into the second level
        Assert.That(() => ShardFiles("state-0"), Is.Empty.After(5000, 20));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ShardFiles("state-0-l2"), Is.Not.Empty);
            Assert.That(SecondLevelStoredStateBytes() - secondLevelStoredBefore, Is.EqualTo(TrieNodeLogRecord.HeaderLength * 3 + 3 + 3000 + 3 + Rlp1.Length),
                "the latest top record and the cold record, each with a 3-byte key, plus a commit record");
            Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.Null);
            Assert.That(Raw().LoadStateRlp(coldPath, ReadFlags.None), Is.Null);
            Assert.That(ReadTop(), Is.EqualTo(Value(2)));
        }

        using IPersistence.IPersistenceReader beforeMedium = _persistence.CreateReader();
        using (IPersistence.IWriteBatch batch = Batch(2, 3))
        {
            batch.SetStateTrieNode(MediumPath, Rlp2);
            batch.SetStateTrieNode(TopPath, Value(3));
        }
        WriteTop(3, 4, Value(4)); // seals first-level generation 2, whose copy fills the second level's generation
        Assert.That(() => Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Value(4)).After(5000, 20));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().LoadStateRlp(coldPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(Raw().LoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp2));
            Assert.That(beforeMedium.LoadStateRlp(MediumPath, ReadFlags.None), Is.Null, "the second-level copy is newer than the reader's version");
            Assert.That(beforeMedium.LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Value(2)));
            Assert.That(beforeMedium.LoadStateRlp(coldPath, ReadFlags.None), Is.EqualTo(Rlp1));
        }

        beforeMedium.Dispose();
        Assert.That(LogFiles, Is.Empty.After(5000, 20));

        // The first level is empty and only the second level holds the latest value when the drain starts.
        WriteTop(4, 5, Value(5));
        WriteTop(5, 6, Value(6));
        Assert.That(() => ShardFiles("state-0"), Is.Empty.After(5000, 20));
        _log.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Value(6)));
            Assert.That(LogFiles, Is.Empty.After(5000, 20));
        }
    }

    [Test]
    public async Task Second_level_merge_lag_is_configured_separately()
    {
        await ReopenWithSecondLevel(secondLevelMergeLag: 1);

        // 3000-byte values: two per 4 KiB generation, so every second batch seals a first-level generation and every
        // fourth a second-level one.
        static byte[] Value(byte seed) => TrieNodeLogTests.Value(seed, 3000);

        TreePath coldPath = TreePath.FromHexString("1234"); // same shard as TopPath, written once
        using (IPersistence.IWriteBatch batch = Batch(0, 1))
        {
            batch.SetStateTrieNode(TopPath, Value(1));
            batch.SetStateTrieNode(coldPath, Rlp1);
        }
        for (ulong block = 1; block < 4; block++) WriteTop(block, block + 1, Value((byte)(block + 1)));
        Assert.That(() => ShardFiles("state-0"), Is.Empty.After(5000, 20), "a first-level lag of zero merges every sealed generation");
        Assert.That(Raw().LoadStateRlp(coldPath, ReadFlags.None), Is.Null, "a second-level lag of one keeps second-level generation 1 until generation 2 is sealed");

        for (ulong block = 4; block < 8; block++) WriteTop(block, block + 1, Value((byte)(block + 1)));
        Assert.That(() => Raw().LoadStateRlp(coldPath, ReadFlags.None), Is.EqualTo(Rlp1).After(5000, 20));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.Null, "rewritten in second-level generation 2, so not merged from generation 1");
            Assert.That(ReadTop(), Is.EqualTo(Value(8)));
        }
    }

    [Test]
    public async Task Second_level_survives_a_restart_and_the_startup_merge_takes_it_first()
    {
        await ReopenWithSecondLevel(secondLevelMergeLag: 0);
        byte[] large = Value(1, 3000);

        using (IPersistence.IWriteBatch batch = Batch(0, 1))
        {
            batch.SetStateTrieNode(TopPath, large);
            batch.SetStateTrieNode(MediumPath, Rlp1);
        }
        WriteTop(1, 2, large); // seals first-level generation 1, copied into the second level
        Assert.That(() => ShardFiles("state-0"), Is.Empty.After(5000, 20));
        WriteTop(2, 3, Rlp3);

        // A torn tail plus a batch whose RocksDB write never happened: the second level is confirmed by the first
        // level's version, so it survives the rollback that drops the last first-level batch.
        await _log.DisposeAsync();
        foreach (string file in LogFiles()) File.AppendAllText(file, "torn tail garbage");
        foreach (TrieNodeLogShard shard in _log.Shards)
        {
            byte[] version = _db.GetColumnDb(FlatDbColumns.Metadata).Get(shard.VersionKey)!;
            version[^1]--;
            _db.GetColumnDb(FlatDbColumns.Metadata).Set(shard.VersionKey, version);
        }
        Open();
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(large));
            Assert.That(reader.LoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp1));
        }

        WriteTop(3, 4, Rlp3);
        await _log.DisposeAsync();
        TrieNodeLog.MergeAllOnDisk(_directory.Path, FileSystem, _db, LimboLogs.Instance);
        Open();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LogFiles(), Is.Empty);
            Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp3), "the first level's newer record is merged after the second level's");
            Assert.That(Raw().LoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp1));
        }
    }

    [Test]
    public void Keys_are_sharded_by_their_first_byte()
    {
        TreePath highPath = TreePath.FromHexString("f1234"); // first key byte 0xf1 lands in the second of two shards
        using (IPersistence.IWriteBatch batch = Batch(0, 1))
        {
            batch.SetStateTrieNode(TopPath, Rlp1);
            batch.SetStateTrieNode(highPath, Rlp2);
        }

        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.GetFiles(Path.Combine(_directory.Path, "state-0")), Is.Not.Empty);
            Assert.That(Directory.GetFiles(Path.Combine(_directory.Path, "state-1")), Is.Not.Empty);
            Assert.That(Directory.GetFiles(Path.Combine(_directory.Path, "storage-0")), Is.Empty, "storage nodes have their own partition");
            Assert.That(reader.LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(reader.LoadStateRlp(highPath, ReadFlags.None), Is.EqualTo(Rlp2));
        }

        _log.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(Raw().LoadStateRlp(highPath, ReadFlags.None), Is.EqualTo(Rlp2));
        }
    }

    [Test]
    public void Persistence_backs_up_on_the_merge_backlog_and_completes()
    {
        // One merge at a time and no slack beyond the lag: every other batch rolls, so rolls regularly have to wait.
        _config.TrieNodeLogMaxConcurrentMerges = 1;
        _config.TrieNodeLogMergeBacklogMargin = 1;
        Reopen().GetAwaiter().GetResult();

        byte[] value = [];
        for (ulong block = 0; block < 24; block++)
        {
            value = Value((byte)block, 3000);
            WriteTop(block, block + 1, value);
            Assert.That(ReadTop(), Is.EqualTo(value));
        }

        _log.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(value));
            Assert.That(LogFiles, Is.Empty.After(5000, 20));
        }
    }

    [Test]
    public async Task Startup_merge_takes_any_shard_layout_into_RocksDB()
    {
        TreePath highPath = TreePath.FromHexString("f1234"); // second state shard
        using (IPersistence.IWriteBatch batch = Batch(0, 1))
        {
            batch.SetStateTrieNode(TopPath, Rlp1);
            batch.SetStateTrieNode(highPath, Rlp2);
            batch.SetStateTrieNode(MediumPath, Rlp3);
        }
        await _log.DisposeAsync();
        Assert.That(Raw().LoadStateRlp(highPath, ReadFlags.None), Is.Null);

        TrieNodeLog.MergeAllOnDisk(_directory.Path, FileSystem, _db, LimboLogs.Instance);

        // The directory is clean, so a different shard count starts from an empty log and reads come from RocksDB.
        _config.TrieNodeLogStateShardCount = 1;
        Open();
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.GetDirectories(_directory.Path).Where(static directory => Directory.GetFiles(directory).Length > 0), Is.Empty);
            Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(Raw().LoadStateRlp(highPath, ReadFlags.None), Is.EqualTo(Rlp2));
            Assert.That(Raw().LoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp3));
            Assert.That(reader.LoadStateRlp(highPath, ReadFlags.None), Is.EqualTo(Rlp2));
        }
    }

    [TestCase(2, true)]
    [TestCase(1, false)]
    [TestCase(4, false)]
    public async Task The_on_disk_layout_matches_the_config_only_with_the_same_shard_count(int stateShardCount, bool matches)
    {
        WriteTop(0, 1, Rlp1);
        await _log.DisposeAsync();
        _config.TrieNodeLogStateShardCount = stateShardCount;

        Assert.That(TrieNodeLog.MatchesOnDiskLayout(_directory.Path, FileSystem, _config), Is.EqualTo(matches));
    }

    [TestCase(true, true, true)]
    [TestCase(true, false, false)]
    [TestCase(false, true, true)]
    [TestCase(false, false, true)]
    public async Task The_on_disk_second_level_matches_the_config_only_when_enabled(bool writtenWithSecondLevel, bool secondLevelEnabled, bool matches)
    {
        // Start from the layout under test rather than the fixture's, whose second-level directories would linger.
        await _log.DisposeAsync();
        foreach (string shardDirectory in Directory.GetDirectories(_directory.Path)) Directory.Delete(shardDirectory, recursive: true);
        _config.TrieNodeLogSecondLevelMergeLag = writtenWithSecondLevel ? 0 : -1;
        Open();
        WriteTop(0, 1, Rlp1);
        await _log.DisposeAsync();
        _config.TrieNodeLogSecondLevelMergeLag = secondLevelEnabled ? 0 : -1;

        Assert.That(TrieNodeLog.MatchesOnDiskLayout(_directory.Path, FileSystem, _config), Is.EqualTo(matches));
    }

    [Test]
    public async Task The_on_disk_layout_does_not_match_with_a_directory_of_another_layout()
    {
        WriteTop(0, 1, Rlp1);
        await _log.DisposeAsync();
        Directory.CreateDirectory(Path.Combine(_directory.Path, "state_top-0"));

        Assert.That(TrieNodeLog.MatchesOnDiskLayout(_directory.Path, FileSystem, _config), Is.False);
    }

    [Test]
    public async Task Draining_on_shutdown_is_opt_in()
    {
        WriteTop(0, 1, Rlp1);
        await _log.DisposeAsync();
        Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.Null, "by default the log is kept for the next start");

        _config.TrieNodeLogDrainOnShutdown = true;
        Open();
        await _log.DisposeAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(LogFiles(), Is.Empty);
        }
    }

    [Test]
    public async Task A_generation_file_of_another_format_is_refused()
    {
        WriteTop(0, 1, Rlp1);
        await _log.DisposeAsync();
        string file = LogFiles().Single(static file => file.Contains("state-0"));
        using (FileStream stream = new(file, FileMode.Open, FileAccess.Write))
        {
            stream.Position = 4; // the version word after the magic
            stream.Write(Bytes.FromHexString("0xffffffff"));
        }

        Assert.That(Open, Throws.TypeOf<InvalidDataException>().With.Message.Contains("format"));
    }

    [Test]
    public async Task A_batch_RocksDB_never_confirmed_poisons_the_log_until_restart()
    {
        byte[] key = Bytes.FromHexString("0x123450"); // a StateTopNodes column key
        using (IColumnsWriteBatch<FlatDbColumns> rocksDbBatch = _db.StartWriteBatch())
        using (ITrieNodeLog.IWriteBatch logBatch = _log.StartWriteBatch(rocksDbBatch, bypass: false))
        {
            logBatch.Wrap(FlatDbColumns.StateTopNodes, rocksDbBatch.GetColumnBatch(FlatDbColumns.StateTopNodes)).PutSpan(key, Rlp1);
            logBatch.Commit();
            rocksDbBatch.Clear(); // the RocksDB write "failed": nothing of this batch, its version included, reaches RocksDB
        }

        Assert.That(() => Batch(0, 1), Throws.InvalidOperationException.With.Message.Contains("restart"));

        await Reopen();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_db.GetColumnDb(FlatDbColumns.StateTopNodes).Get(key), Is.Null);
            Assert.That(() => Batch(0, 1).Dispose(), Throws.Nothing);
        }
    }

    [Test]
    public async Task An_aborted_batch_leaves_no_record_and_the_log_takes_the_next_batch([Values] bool beyondOneStagingChunk)
    {
        WriteTop(0, 1, Rlp1);
        TreePath[] paths = WriteAbortedBatch(beyondOneStagingChunk ? 24 : 1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadTop(), Is.EqualTo(Rlp1));
            Assert.That(paths.Select(ReadState), Has.All.Null, "no aborted record is readable");
            Assert.That(LogFiles().Select(static file => new FileInfo(file).Length), Has.All.LessThan(AbortedRecordLength), "the appended records are truncated");
            Assert.That(BasePersistence.ReadCurrentState(_db.GetColumnDb(FlatDbColumns.Metadata)), Is.EqualTo(State(1)));
        }

        WriteTop(1, 2, Rlp3);
        Assert.That(ReadTop(), Is.EqualTo(Rlp3), "the log takes the next batch");

        await Reopen();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadTop(), Is.EqualTo(Rlp3));
            Assert.That(paths.Select(ReadState), Has.All.Null, "no aborted record is recovered");
        }
    }

    // A power loss keeps the directory entry of a file whose content was never synced, but can drop that content. The
    // generations an aborted batch rolled hold only their header, and the next commit confirms a newer generation of the
    // shard; unless those headers were synced, the first open after the loss drops the emptied files and the next open finds
    // generations the database confirms missing.
    [Test]
    public async Task Generations_an_aborted_batch_rolled_survive_a_power_loss_after_the_next_commit()
    {
        FileSystemProbe probe = new();
        await _log.DisposeAsync();
        OpenProbed(probe);
        // A generation's header is durable before the roll publishes it, and so before the next one is created.
        probe.BeforeFlush = (path, length) =>
        {
            if (length == TrieNodeLogGeneration.FileHeaderLength && _log.Shards.Any(shard => shard.Active is { } active && Path.GetFullPath(active.Path) == path))
                probe.OrderViolations.Enqueue($"{path} was published before its header was synced");
        };

        WriteTop(0, 1, Rlp1);
        WriteAbortedBatch(24);
        WriteTop(1, 2, Rlp3);
        await _log.DisposeAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(LogFiles().Select(static file => new FileInfo(file).Length), Has.Some.EqualTo(TrieNodeLogGeneration.FileHeaderLength),
                "the aborted batch left header-only generations between the two committed ones");
            Assert.That(probe.OrderViolations, Is.Empty);
        }
        LoseUnsyncedHeaders(probe);

        Open();
        Assert.That(ReadTop(), Is.EqualTo(Rlp3), "the first open after the loss");
        await Reopen();
        Assert.That(ReadTop(), Is.EqualTo(Rlp3), "the second open after the loss");
    }

    // A process that exits between a roll's header write and its sync leaves a header-only file the OS still holds. The
    // warm restart that adopts it must make it durable, as the next commit confirms a newer generation of the shard.
    [Test]
    public async Task A_header_only_generation_adopted_on_restart_survives_a_power_loss_after_the_next_commit()
    {
        FileSystemProbe probe = new();
        await _log.DisposeAsync();
        OpenProbed(probe);
        WriteTop(0, 1, Rlp1);
        await _log.DisposeAsync();

        // The file image of the exit: generation 2 holds its header, never synced.
        string path = Path.Combine(_directory.Path, "state-0", "gen-00000002.log");
        using (TrieNodeLogGeneration interrupted = new(2, path, FileSystem, 1024, static (_, exception) => throw exception))
        {
            interrupted.WriteFileHeader();
            interrupted.PreserveOnDispose();
        }

        OpenProbed(probe);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(probe.DurableLength.GetValueOrDefault(Path.GetFullPath(path)), Is.GreaterThanOrEqualTo(TrieNodeLogGeneration.FileHeaderLength),
                "the open made the adopted header durable");
            Assert.That(ReadTop(), Is.EqualTo(Rlp1), "the warm restart");
        }
        WriteTop(1, 2, Rlp3);
        await _log.DisposeAsync();
        LoseUnsyncedHeaders(probe);

        Open();
        Assert.That(ReadTop(), Is.EqualTo(Rlp3), "the first open after the loss");
        await Reopen();
        Assert.That(ReadTop(), Is.EqualTo(Rlp3), "the second open after the loss");
    }

    public enum RollFault
    {
        HeaderWrite,
        PartialHeaderWrite,
        HeaderSync,
    }

    // A roll whose header write or sync fails leaves its number unused and its file behind. The next roll takes the same
    // number and file name and rewrites the file with a full header and a sync, so the failed roll must not delete the
    // file, now or through a deletion queued for later, which would remove the generation that next roll commits.
    [Test]
    public async Task A_failed_roll_is_retried_into_the_same_number([Values] RollFault fault)
    {
        IOException failure = new("roll failed");
        int fired = 0;
        bool FirstFault(string path) => IsStateGeneration(path, 1) && Interlocked.Exchange(ref fired, 1) == 0;
        FileSystemProbe probe = new()
        {
            FailWrite = (path, offset, length) => fault != RollFault.HeaderSync && offset == 0 && length == TrieNodeLogGeneration.FileHeaderLength && FirstFault(path)
                ? (failure, fault == RollFault.PartialHeaderWrite ? TrieNodeLogGeneration.FileHeaderLength / 2 : 0)
                : null,
            FailFlush = (path, length) => fault == RollFault.HeaderSync && length == TrieNodeLogGeneration.FileHeaderLength && FirstFault(path) ? failure : null,
            FailDelete = static path => IsStateGeneration(path, 1),
        };
        _config.TrieNodeLogSecondLevelMergeLag = -1; // a merge goes straight to RocksDB, whose trie column flush fails below
        await _log.DisposeAsync();
        OpenProbed(probe, logDb: WithUnflushable(static column => column != FlatDbColumns.Metadata));

        IOException? thrown = Assert.Throws<IOException>(() => WriteTop(0, 1, Rlp1));
        WriteTop(0, 1, Rlp3);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, Is.SameAs(failure), "the roll's own failure reaches the caller");
            Assert.That(_log.Shards[0].Active?.Number, Is.EqualTo(1UL), "the retry rolled into the same number, with no restart");
            Assert.That(ReadTop(), Is.EqualTo(Rlp3));
        }

        // A merge pass first retries the deletions queued for later; its flush fails, so the merged marker stays below the
        // recorded generation and the file the retry committed into is still required.
        Assert.That(() => _log.Drain(), Throws.TypeOf<AggregateException>().With.InnerException.TypeOf<IOException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(probe.DeleteAttempts.GetValueOrDefault(Path.GetFullPath(Path.Combine(_directory.Path, "state-0", "gen-00000001.log"))), Is.Zero,
                "the failed roll's file is never deleted");
            Assert.That(FlushedGeneration(_log.Shards[0]), Is.LessThan(RecordedGeneration(_log.Shards[0])));
        }

        await _log.DisposeAsync();
        Open();
        Assert.That(ReadTop(), Is.EqualTo(Rlp3), "the first open");
        await Reopen();
        Assert.That(ReadTop(), Is.EqualTo(Rlp3), "the second open");
    }

    // A batch aborted after rolling leaves the generations before its last roll unsealed, the oldest of them holding the only
    // copy of a committed record. A drain merges every generation in order, so its marker never passes over that record.
    [Test]
    public async Task A_drain_after_a_rolled_abort_merges_every_generation([Values] bool secondLevel)
    {
        _config.TrieNodeLogMergeLag = 1000; // nothing is merged before the drain
        _config.TrieNodeLogSecondLevelMergeLag = secondLevel ? 1000 : -1;
        await Reopen();

        WriteTop(0, 1, Rlp1);
        Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.Null, "the committed node is only in the log");
        int filesBefore = ShardFiles("state-0").Length;
        TreePath[] aborted = WriteAbortedBatch(24);
        string[] files = ShardFiles("state-0");
        Assert.That(files, Has.Length.GreaterThan(filesBefore), "the aborted batch rolled");
        ulong lastGeneration = files.Max(GenerationNumber);

        _persistence.Flush();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1), "the drain merged the committed node into RocksDB");
            Assert.That(ReadTop(), Is.EqualTo(Rlp1));
            Assert.That(aborted.Select(ReadState), Has.All.Null);
            Assert.That(FlushedGeneration(_log.Shards[0]), Is.EqualTo(lastGeneration), "the marker covers the last drained file");
        }

        await Reopen();
        Assert.That(ReadTop(), Is.EqualTo(Rlp1), "the first open");
        await Reopen();
        Assert.That(ReadTop(), Is.EqualTo(Rlp1), "the second open");
    }

    // A first-level merge copies a sealed generation into the second level, whose roll can fail. The copy is retried, and a
    // write waiting on the merge backlog completes once it succeeds, with no restart.
    [Test]
    public async Task A_failed_second_level_copy_is_retried_and_the_waiting_write_completes()
    {
        IOException failure = new("second-level header sync failed");
        using ManualResetEventSlim copyFailed = new();
        int fired = 0;
        FileSystemProbe probe = new()
        {
            FailFlush = (path, length) =>
            {
                if (length != TrieNodeLogGeneration.FileHeaderLength || ShardOf(path) != "state-0-l2" || Interlocked.Exchange(ref fired, 1) != 0) return null;
                copyFailed.Set();
                return failure;
            },
            FailDelete = static path => ShardOf(path) == "state-0-l2",
        };
        _config.TrieNodeLogMergeBacklogMargin = 1; // one sealed, unmerged generation makes the next roll wait
        _config.TrieNodeLogSecondLevelMergeLag = 1000;
        await _log.DisposeAsync();
        OpenProbed(probe, mergeLag: 0);

        // Fills its generation, so the commit seals it and the merge copies it at once.
        byte[] first = Value(1, 5000);
        WriteTop(0, 1, first);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(copyFailed.Wait(TimeSpan.FromSeconds(10)), Is.True, "the copy reached the second level's roll");
            Assert.That(FlushedGeneration(_log.Shards[0]), Is.Zero, "a failed copy leaves the source unmerged");
        }

        TreePath secondPath = TreePath.FromHexString("1abcd");
        byte[] second = Value(2, 5000);
        Task write = Task.Run(() =>
        {
            using IPersistence.IWriteBatch batch = Batch(1, 2);
            batch.SetStateTrieNode(secondPath, second);
        });
        Assert.That(write.Wait(TimeSpan.FromSeconds(20)), Is.True, "the write waiting on the backlog completed, with no restart");

        await _log.DisposeAsync();
        LoseUnsyncedHeaders(probe);
        Open();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadTop(), Is.EqualTo(first), "the first open");
            Assert.That(ReadState(secondPath), Is.EqualTo(second), "the first open");
        }
        await Reopen();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadTop(), Is.EqualTo(first), "the second open");
            Assert.That(ReadState(secondPath), Is.EqualTo(second), "the second open");
        }
    }

    // A commit can sync its records into one file and fail to sync them into the next. Its rollback truncates both files
    // without a sync, so after a power loss the first file may still end with the failed version's commit record, for a
    // later version to confirm. The shard takes no batch until a restart, whose recovery cuts that tail.
    [Test]
    public async Task A_commit_that_synced_only_part_of_its_records_stops_the_shard_until_a_restart()
    {
        IOException failure = new("commit record sync failed");
        int fired = 0;
        FileSystemProbe probe = new()
        {
            FailFlush = (path, length) => length > TrieNodeLogGeneration.FileHeaderLength && IsStateGeneration(path, 2) && Interlocked.Exchange(ref fired, 1) == 0 ? failure : null,
        };
        await _log.DisposeAsync();
        OpenProbed(probe);
        WriteTop(0, 1, Rlp1); // the confirmed value, at version 1 in generation 1
        string firstFile = Path.GetFullPath(Path.Combine(_directory.Path, "state-0", "gen-00000001.log"));
        TreePath rolledPath = TreePath.FromHexString("12346"); // the same shard as the top node
        TreePath unrelatedPath = TreePath.FromHexString("1abcd"); // the same shard again

        // Version 2 overwrites the value with one that fills generation 1, so its next record rolls into generation 2.
        Exception? thrown = Assert.Catch(() =>
        {
            using IPersistence.IWriteBatch batch = Batch(1, 2);
            batch.SetStateTrieNode(TopPath, Value(9, 6000));
            batch.SetStateTrieNode(rolledPath, Rlp2);
        });
        byte[] syncedFirstFile = probe.DurableImage[firstFile];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown is AggregateException aggregate ? aggregate.InnerException : thrown, Is.SameAs(failure));
            Assert.That(ShardFiles("state-0"), Has.Length.EqualTo(2), "the version-2 batch rolled into generation 2");
            Assert.That(syncedFirstFile, Has.Length.GreaterThan(6000), "generation 1 was synced with the failed version's records");
            Assert.That(() => WriteTop(1, 2, Rlp3), Throws.InvalidOperationException.With.Message.Contains("restart"), "a version-advancing write is refused until a restart");
        }

        // The power loss: generation 1's truncation by the rollback was never synced.
        await _log.DisposeAsync();
        File.WriteAllBytes(firstFile, syncedFirstFile);
        Open();
        Assert.That(ReadTop(), Is.EqualTo(Rlp1), "the first open");
        using (IPersistence.IWriteBatch batch = Batch(1, 2)) batch.SetStateTrieNode(unrelatedPath, Rlp3);
        await Reopen();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadTop(), Is.EqualTo(Rlp1), "the second open, after a later commit");
            Assert.That(ReadState(unrelatedPath), Is.EqualTo(Rlp3));
        }
    }

    /// <summary>Whether <paramref name="path"/> is generation <paramref name="number"/> of the first state shard.</summary>
    private static bool IsStateGeneration(string path, ulong number) =>
        ShardOf(path) == "state-0" && Path.GetFileName(path) == $"gen-{number:D8}.log";

    private static string? ShardOf(string path) => Path.GetFileName(Path.GetDirectoryName(path));

    private static ulong GenerationNumber(string path) => ulong.Parse(Path.GetFileNameWithoutExtension(path)["gen-".Length..]);

    private ulong FlushedGeneration(TrieNodeLogShard shard) => ReadMetadata(shard.FlushedGenerationKey);

    private ulong RecordedGeneration(TrieNodeLogShard shard) => ReadMetadata(shard.GenerationKey);

    private ulong ReadMetadata(byte[] key) =>
        _db.GetColumnDb(FlatDbColumns.Metadata).Get(key) is { Length: 8 } bytes ? BinaryPrimitives.ReadUInt64BigEndian(bytes) : 0;

    private const int AbortedRecordLength = 60_000;

    /// <summary>
    /// Aborts a batch from state 1 that overwrites the top node and writes <paramref name="keys"/> large records to the
    /// first state shard; past one 1 MiB staging chunk the records are appended to its files, rolling generations, before
    /// the batch is disposed.
    /// </summary>
    /// <returns>The paths of the large records.</returns>
    private TreePath[] WriteAbortedBatch(int keys)
    {
        TreePath[] paths = new TreePath[keys];
        for (int i = 0; i < keys; i++) paths[i] = TreePath.FromHexString($"1{i:x4}");
        using IPersistence.IWriteBatch batch = Batch(1, 2);
        batch.SetStateTrieNode(TopPath, Rlp2);
        for (int i = 0; i < keys; i++) batch.SetStateTrieNode(paths[i], Value((byte)i, AbortedRecordLength));
        batch.Abort();
        return paths;
    }

    private byte[]? ReadState(TreePath path)
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        return reader.LoadStateRlp(path, ReadFlags.None);
    }

    /// <summary>
    /// Opens the log over the real file system as <paramref name="probe"/> observes and faults it, with a merge lag by
    /// default so high that nothing is merged and every generation stays confirmed.
    /// </summary>
    /// <param name="logDb">The database the log itself uses; the store always uses the test database.</param>
    private void OpenProbed(FileSystemProbe probe, int mergeLag = 1000, IColumnsDb<FlatDbColumns>? logDb = null)
    {
        _config.TrieNodeLogMergeLag = mergeLag;
        _log = new TrieNodeLog(_directory.Path, ProbedFileSystem(probe), logDb ?? _db, _config, LimboLogs.Instance);
        _persistence = new RocksDbPersistence(_db, LimboLogs.Instance, _log);
    }

    /// <summary>Models a power loss: a log file whose header no flush made durable keeps its directory entry and loses its content.</summary>
    private void LoseUnsyncedHeaders(FileSystemProbe probe)
    {
        foreach (string file in LogFiles())
        {
            if (probe.DurableLength.GetValueOrDefault(Path.GetFullPath(file)) < TrieNodeLogGeneration.FileHeaderLength)
                File.WriteAllBytes(file, Array.Empty<byte>());
        }
    }

    /// <summary>What <see cref="ProbedFileSystem"/> observed, and the faults it injects. Paths are full paths.</summary>
    private sealed class FileSystemProbe
    {
        /// <summary>Per file, the longest file length a successful flush made durable.</summary>
        public ConcurrentDictionary<string, long> DurableLength { get; } = new();

        /// <summary>Per file, its content at its last successful flush.</summary>
        public ConcurrentDictionary<string, byte[]> DurableImage { get; } = new();

        /// <summary>Per file, how many times its deletion was attempted.</summary>
        public ConcurrentDictionary<string, int> DeleteAttempts { get; } = new();

        /// <summary>A generation file created before its predecessor's header was durable, or flushed after it was published.</summary>
        public ConcurrentQueue<string> OrderViolations { get; } = new();

        /// <summary>The exception the flush of a file (by path and length) throws instead of flushing, or null.</summary>
        public Func<string, long, Exception?> FailFlush { get; init; } = static (_, _) => null;

        /// <summary>The exception a write (by path, offset and length) throws instead, after writing a prefix of the given length; or null.</summary>
        public Func<string, long, int, (Exception Failure, int Prefix)?> FailWrite { get; init; } = static (_, _, _) => null;

        /// <summary>Whether the deletion of a file throws instead of deleting.</summary>
        public Func<string, bool> FailDelete { get; init; } = static _ => false;

        /// <summary>Called with each file and its length before it is flushed.</summary>
        public Action<string, long>? BeforeFlush { get; set; }

        private readonly ConcurrentDictionary<string, byte> _created = new();

        /// <summary>Records a generation file being created, checking that the previous one of its shard has a durable header.</summary>
        public void OnCreated(string path)
        {
            if (Path.GetFileName(path).StartsWith("gen-", StringComparison.Ordinal))
            {
                string previous = Path.Combine(Path.GetDirectoryName(path)!, $"gen-{GenerationNumber(path) - 1:D8}.log");
                if (_created.ContainsKey(previous) && DurableLength.GetValueOrDefault(previous) < TrieNodeLogGeneration.FileHeaderLength)
                    OrderViolations.Enqueue($"{path} was created before the header of {previous} was durable");
            }
            _created[path] = 0;
        }
    }

    /// <summary>The real file system, as <paramref name="probe"/> observes and faults it.</summary>
    private static IFileSystem ProbedFileSystem(FileSystemProbe probe)
    {
        ConcurrentDictionary<SafeFileHandle, string> paths = new();
        IFile file = Substitute.For<IFile>();
        file.OpenHandle(Arg.Any<string>(), Arg.Any<FileMode>(), Arg.Any<FileAccess>(), Arg.Any<FileShare>(), Arg.Any<FileOptions>(), Arg.Any<long>())
            .Returns(call =>
            {
                string path = Path.GetFullPath(call.ArgAt<string>(0));
                bool created = call.ArgAt<FileMode>(1) != FileMode.Open && !FileSystem.File.Exists(path);
                SafeFileHandle handle = FileSystem.File.OpenHandle(path, call.ArgAt<FileMode>(1), call.ArgAt<FileAccess>(2), call.ArgAt<FileShare>(3), call.ArgAt<FileOptions>(4), call.ArgAt<long>(5));
                paths[handle] = path;
                if (created) probe.OnCreated(path);
                return handle;
            });
        file.Exists(Arg.Any<string?>()).Returns(static call => FileSystem.File.Exists(call.ArgAt<string?>(0)));
        file.When(static f => f.Delete(Arg.Any<string>())).Do(call =>
        {
            string path = Path.GetFullPath(call.ArgAt<string>(0));
            probe.DeleteAttempts.AddOrUpdate(path, 1, static (_, attempts) => attempts + 1);
            if (probe.FailDelete(path)) throw new IOException($"deleting {path} failed");
            FileSystem.File.Delete(path);
        });

        IFileSystem fileSystem = Substitute.For<IFileSystem>();
        fileSystem.File.Returns(file);
        fileSystem.Directory.Returns(FileSystem.Directory);
        fileSystem.FileInfo.Returns(FileSystem.FileInfo);
        fileSystem.Path.Returns(FileSystem.Path);
        fileSystem.RandomAccess.Returns(new ProbedRandomAccess(FileSystem.RandomAccess, probe, paths));
        return fileSystem;
    }

    /// <summary>Forwards to the real random access; a flush records what it made durable, and a flush or write may throw the probe's fault.</summary>
    private sealed class ProbedRandomAccess(IRandomAccess inner, FileSystemProbe probe, ConcurrentDictionary<SafeFileHandle, string> paths) : IRandomAccess
    {
        public IFileSystem FileSystem => inner.FileSystem;

        public void FlushToDisk(SafeFileHandle handle)
        {
            string path = paths[handle];
            long length = inner.GetLength(handle);
            probe.BeforeFlush?.Invoke(path, length);
            if (probe.FailFlush(path, length) is { } failure) throw failure;
            inner.FlushToDisk(handle);

            byte[] image = new byte[length];
            int read = 0;
            while (read < image.Length && inner.Read(handle, image.AsSpan(read), read) is > 0 and int count) read += count;
            probe.DurableImage[path] = image[..read];
            probe.DurableLength.AddOrUpdate(path, length, (_, durable) => Math.Max(durable, length));
        }

        public void Write(SafeFileHandle handle, ReadOnlySpan<byte> buffer, long fileOffset)
        {
            if (probe.FailWrite(paths[handle], fileOffset, buffer.Length) is { } fault)
            {
                if (fault.Prefix > 0) inner.Write(handle, buffer[..fault.Prefix], fileOffset);
                throw fault.Failure;
            }
            inner.Write(handle, buffer, fileOffset);
        }

        public long GetLength(SafeFileHandle handle) => inner.GetLength(handle);
        public int Read(SafeFileHandle handle, Span<byte> buffer, long fileOffset) => inner.Read(handle, buffer, fileOffset);
        public long Read(SafeFileHandle handle, IReadOnlyList<Memory<byte>> buffers, long fileOffset) => inner.Read(handle, buffers, fileOffset);
        public ValueTask<int> ReadAsync(SafeFileHandle handle, Memory<byte> buffer, long fileOffset, CancellationToken cancellationToken = default) => inner.ReadAsync(handle, buffer, fileOffset, cancellationToken);
        public ValueTask<long> ReadAsync(SafeFileHandle handle, IReadOnlyList<Memory<byte>> buffers, long fileOffset, CancellationToken cancellationToken = default) => inner.ReadAsync(handle, buffers, fileOffset, cancellationToken);
        public void SetLength(SafeFileHandle handle, long length) => inner.SetLength(handle, length);
        public void Write(SafeFileHandle handle, IReadOnlyList<ReadOnlyMemory<byte>> buffers, long fileOffset) => inner.Write(handle, buffers, fileOffset);
        public ValueTask WriteAsync(SafeFileHandle handle, ReadOnlyMemory<byte> buffer, long fileOffset, CancellationToken cancellationToken = default) => inner.WriteAsync(handle, buffer, fileOffset, cancellationToken);
        public ValueTask WriteAsync(SafeFileHandle handle, IReadOnlyList<ReadOnlyMemory<byte>> buffers, long fileOffset, CancellationToken cancellationToken = default) => inner.WriteAsync(handle, buffers, fileOffset, cancellationToken);
    }

    [Test]
    public void A_commit_failing_after_the_log_is_durable_poisons_the_log()
    {
        using (IColumnsWriteBatch<FlatDbColumns> rocksDbBatch = _db.StartWriteBatch())
        using (ITrieNodeLog.IWriteBatch logBatch = _log.StartWriteBatch(new FailingMetadataBatch(rocksDbBatch), bypass: false))
        {
            logBatch.Wrap(FlatDbColumns.StateTopNodes, rocksDbBatch.GetColumnBatch(FlatDbColumns.StateTopNodes)).PutSpan(Bytes.FromHexString("0x123450"), Rlp1);
            Assert.That(logBatch.Commit, Throws.TypeOf<IOException>(), "the version write into the RocksDB batch fails after the log records are fsynced");
        }

        Assert.That(() => Batch(0, 1), Throws.InvalidOperationException.With.Message.Contains("restart"));
    }

    [Test]
    public void A_sync_batch_range_delete_removes_nodes_the_log_held()
    {
        WriteTop(0, 1, Rlp1);
        using (IPersistence.IWriteBatch sync = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            sync.DeleteStateTrieNodeRange(new ValueHash256("0x1000000000000000000000000000000000000000000000000000000000000000"), new ValueHash256("0x2000000000000000000000000000000000000000000000000000000000000000"));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.Null, "the drain preceded the snapshot the range delete scanned");
            Assert.That(ReadTop(), Is.Null);
        }
    }

    [Test]
    public void A_generation_committed_before_the_snapshot_and_merged_right_after_stays_readable()
    {
        // Between a reader pinning the live generations and binding to its snapshot: a batch commits into a new
        // generation, the snapshot is taken, and a drain merges and deletes that generation.
        Task drain = Task.CompletedTask;
        IColumnsDb<FlatDbColumns> db = HookedDb(() =>
        {
            WriteTop(0, 1, Rlp1);
            IColumnDbSnapshot<FlatDbColumns> snapshot = _db.CreateSnapshot();
            drain = Task.Run(() => _log.Drain());
            SpinWait.SpinUntil(() => drain.IsCompleted, TimeSpan.FromMilliseconds(500));
            return snapshot;
        });

        using (ITrieNodeLog.IView view = _log.OpenView(db, ReaderFlags.None))
        {
            Assert.That(view.GetColumn(FlatDbColumns.StateTopNodes).Get(Bytes.FromHexString("0x123455")), Is.EqualTo(Rlp1)); // TopPath's column key
        }
        Assert.That(drain.Wait(TimeSpan.FromSeconds(5)), Is.True);
    }

    [Test]
    public void A_view_that_fails_to_bind_releases_its_generations()
    {
        WriteTop(0, 1, Rlp1);
        IColumnDbSnapshot<FlatDbColumns> snapshot = Substitute.For<IColumnDbSnapshot<FlatDbColumns>>();
        snapshot.GetColumn(Arg.Any<FlatDbColumns>()).Returns(static _ => throw new IOException("metadata read failed"));

        Assert.That(() => _log.OpenView(HookedDb(() => snapshot), ReaderFlags.None), Throws.TypeOf<IOException>());
        _log.Drain();
        Assert.That(LogFiles, Is.Empty.After(5000, 20), "a leaked lease would keep the merged file");
    }

    [Test]
    public void A_failed_log_commit_releases_the_RocksDB_batch()
    {
        FailingMetadataBatch? batch = null;
        IColumnsDb<FlatDbColumns> db = Substitute.For<IColumnsDb<FlatDbColumns>>();
        db.GetColumnDb(Arg.Any<FlatDbColumns>()).Returns(call => _db.GetColumnDb(call.Arg<FlatDbColumns>()));
        db.CreateSnapshot().Returns(_ => _db.CreateSnapshot());
        db.StartWriteBatch().Returns(_ => batch = new FailingMetadataBatch(_db.StartWriteBatch()));
        RocksDbPersistence persistence = new(db, LimboLogs.Instance, _log);

        IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(State(0), State(1), WriteFlags.None);
        writeBatch.SetStateTrieNode(TopPath, Rlp1);
        Assert.That(writeBatch.Dispose, Throws.TypeOf<IOException>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch!.Cleared, Is.True);
            Assert.That(batch.Disposed, Is.True);
        }
    }

    [Test]
    public async Task A_database_restored_without_its_log_directory_is_refused([Values] bool logDisabledAtRestart)
    {
        WriteTop(0, 1, Rlp1);
        await _log.DisposeAsync();
        foreach (string shardDirectory in Directory.GetDirectories(_directory.Path)) Directory.Delete(shardDirectory, recursive: true);

        // A disabled log merges whatever is on disk at startup instead of recovering it.
        Action restart = logDisabledAtRestart ? () => TrieNodeLog.MergeAllOnDisk(_directory.Path, FileSystem, _db, LimboLogs.Instance) : Open;
        Assert.That(restart, Throws.TypeOf<InvalidDataException>().With.Message.Contains("missing"));
    }

    [Test]
    public async Task A_database_restored_without_a_partition_is_refused_under_another_shard_count()
    {
        TreePath highPath = TreePath.FromHexString("f1234"); // second state shard
        using (IPersistence.IWriteBatch batch = Batch(0, 1))
        {
            batch.SetStateTrieNode(highPath, Rlp1);
        }
        await _log.DisposeAsync();
        foreach (string shardDirectory in Directory.GetDirectories(_directory.Path, "state-*")) Directory.Delete(shardDirectory, recursive: true);
        _config.TrieNodeLogStateShardCount = 1; // state-1 is no longer a configured shard, only a recorded one

        Assert.That(Open, Throws.TypeOf<InvalidDataException>().With.Message.Contains("state-1"));
    }

    [Test]
    public async Task A_cleared_log_resumes_and_restarts_without_the_discarded_generations()
    {
        WriteTop(0, 1, Rlp1);
        _persistence.Clear();
        WriteTop(0, 1, Rlp2); // a new generation, numbered after the discarded one

        await Reopen();
        Assert.That(ReadTop(), Is.EqualTo(Rlp2));
    }

    [Test]
    public async Task A_wipe_interrupted_before_the_log_was_cleared_restarts_after_the_resync()
    {
        WriteTop(0, 1, Rlp1);
        await _log.DisposeAsync();
        BasePersistence.MarkWipeStarted(_db); // the crash comes before the log is cleared
        Open();
        _persistence.Clear(); // the wipe is redone
        Batch(0, 1).Dispose(); // the resync completes without a new generation in the shard that held Rlp1

        await Reopen();
        Assert.That(ReadTop(), Is.Null);
    }

    [Test]
    public async Task Disposal_completes_while_second_level_merges_keep_failing([Values] bool drainOnShutdown)
    {
        _config.TrieNodeLogDrainOnShutdown = drainOnShutdown;
        await SaturateSecondLevel();

        await Task.Run(() => _log.DisposeAsync().AsTask()).WaitAsync(TimeSpan.FromSeconds(10)); // the drain runs synchronously

        Open(); // the generations that could not be merged were kept
        for (ulong block = 0; block < 3; block++) Assert.That(ReadNode(block), Is.EqualTo(Value((byte)block, 4096)));
    }

    [Test]
    public async Task A_shutdown_drain_merges_a_full_second_level_itself()
    {
        await _log.DisposeAsync();
        _config.TrieNodeLogDrainOnShutdown = true;
        _config.TrieNodeLogMergeLag = 8; // the first level keeps its generations until the drain
        _config.TrieNodeLogSecondLevelMergeLag = 0;
        Open();
        WriteFullGenerations(5); // the drain copies them into more second-level generations than its backlog allows

        await _log.DisposeAsync();
        using (Assert.EnterMultipleScope())
        {
            for (ulong block = 0; block < 5; block++) Assert.That(Raw().LoadStateRlp(DistinctPath(block), ReadFlags.None), Is.EqualTo(Value((byte)block, 4096)));
            Assert.That(LogFiles(), Is.Empty);
        }
    }

    [Test]
    public async Task A_failed_manifest_write_releases_the_recovered_shards()
    {
        await SaturateSecondLevel();
        await _log.DisposeAsync();
        long alive = TrieNodeLogGeneration.AliveCount;

        // Every flush fails: the recovered first-level copy waits on the second level's full backlog, and the manifest
        // cannot be made durable.
        Assert.That(async () => await Task.Run(() => new TrieNodeLog(_directory.Path, FileSystem, WithUnflushable(static _ => true), _config, LimboLogs.Instance)).WaitAsync(TimeSpan.FromSeconds(10)), Throws.TypeOf<IOException>());
        Assert.That(TrieNodeLogGeneration.AliveCount, Is.EqualTo(alive), "the recovered generations are released");

        Open();
        Assert.That(ReadNode(2), Is.EqualTo(Value(2, 4096)));
    }

    [Test]
    public void A_WAL_sync_failure_does_not_confirm_the_log()
    {
        RocksDbPersistence persistence = new(WithFailingWalSync(), LimboLogs.Instance, _log);

        IPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(State(0), State(1), WriteFlags.None);
        writeBatch.SetStateTrieNode(TopPath, Rlp1);
        Assert.That(writeBatch.Dispose, Throws.TypeOf<IOException>());
        Assert.That(() => Batch(1, 2), Throws.InvalidOperationException.With.Message.Contains("restart"), "the batch's version was never confirmed");
    }

    [Test]
    public void Without_the_log_the_WAL_flush_keeps_its_error_handling()
    {
        IColumnsDb<FlatDbColumns> db = WithFailingWalSync();
        RocksDbPersistence persistence = new(db, LimboLogs.Instance, NullTrieNodeLog.Instance);

        persistence.CreateWriteBatch(State(0), State(1), WriteFlags.None).Dispose();
        db.Received(1).Flush(onlyWal: true);
    }

    /// <summary>The test database with a throwing WAL sync.</summary>
    private IColumnsDb<FlatDbColumns> WithFailingWalSync()
    {
        IColumnsDb<FlatDbColumns> db = Substitute.For<IColumnsDb<FlatDbColumns>>();
        db.GetColumnDb(Arg.Any<FlatDbColumns>()).Returns(call => _db.GetColumnDb(call.Arg<FlatDbColumns>()));
        db.CreateSnapshot().Returns(_ => _db.CreateSnapshot());
        db.StartWriteBatch().Returns(_ => _db.StartWriteBatch());
        db.When(static db => db.SyncWal()).Do(static _ => throw new IOException("WAL sync failed"));
        return db;
    }

    [Test]
    public async Task A_database_restored_without_its_second_level_directory_is_refused()
    {
        await ReopenWithSecondLevel(secondLevelMergeLag: 0);
        WriteTop(0, 1, Value(1, 3000));
        WriteTop(1, 2, Value(2, 3000)); // seals first-level generation 1, copied into the second level
        Assert.That(() => ShardFiles("state-0"), Is.Empty.After(5000, 20));
        await _log.DisposeAsync();
        foreach (string file in ShardFiles("state-0-l2")) File.Delete(file);

        Assert.That(Open, Throws.TypeOf<InvalidDataException>().With.Message.Contains("state-0-l2"));
    }

    [Test]
    public void Clear_marks_the_wipe_before_dropping_the_log()
    {
        WriteTop(0, 1, Rlp1);
        RocksDbPersistence persistence = new(WithUnflushable(static column => column == FlatDbColumns.Metadata), LimboLogs.Instance, _log);

        Assert.That(persistence.Clear, Throws.TypeOf<IOException>(), "the wipe marker could not be made durable");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LogFiles(), Is.Not.Empty, "nothing is dropped before the marker is durable");
            Assert.That(ReadTop(), Is.EqualTo(Rlp1));
        }
    }

    [Test]
    [Platform(Exclude = "Win")]
    public void A_merged_file_whose_deletion_failed_is_deleted_on_a_later_merge()
    {
        Assume.That(Environment.UserName, Is.Not.EqualTo("root"), "root can delete from a read-only directory");
        WriteTop(0, 1, Rlp1);
        string shardDirectory = Path.Combine(_directory.Path, "state-0");
        long undeletedBefore = Metrics.TrieNodeLogUndeletedFiles;
        IPersistence.IPersistenceReader reader = _persistence.CreateReader(); // holds the generation past its merge
        _log.Drain();

        SetUnixFileMode(shardDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            reader.Dispose();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(ShardFiles("state-0"), Is.Not.Empty);
                Assert.That(Metrics.TrieNodeLogUndeletedFiles, Is.EqualTo(undeletedBefore + 1));
            }
        }
        finally
        {
            SetUnixFileMode(shardDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        WriteTop(1, 2, Rlp2);
        _log.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ShardFiles("state-0"), Is.Empty);
            Assert.That(Metrics.TrieNodeLogUndeletedFiles, Is.EqualTo(undeletedBefore));
        }
    }

    private static void SetUnixFileMode(string path, UnixFileMode mode)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode);
    }

    /// <summary>A store whose durable flush always fails.</summary>
    private sealed class UnflushableDb(IDb inner) : IDb
    {
        public string Name => inner.Name;
        public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => inner.GetAll(ordered);
        public IEnumerable<byte[]> GetAllKeys(bool ordered = false) => inner.GetAllKeys(ordered);
        public IEnumerable<byte[]> GetAllValues(bool ordered = false) => inner.GetAllValues(ordered);
        public IWriteBatch StartWriteBatch() => inner.StartWriteBatch();
        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => inner.Get(key, flags);
        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) => inner.Set(key, value, flags);
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void FlushOrThrow() => throw new IOException("durable flush failed");
        public void Dispose() => inner.Dispose();
    }

    /// <summary>The test database with the durable flush of every <paramref name="unflushable"/> column failing.</summary>
    private IColumnsDb<FlatDbColumns> WithUnflushable(Func<FlatDbColumns, bool> unflushable)
    {
        IColumnsDb<FlatDbColumns> db = Substitute.For<IColumnsDb<FlatDbColumns>>();
        db.GetColumnDb(Arg.Any<FlatDbColumns>()).Returns(call =>
        {
            IDb column = _db.GetColumnDb(call.Arg<FlatDbColumns>());
            return unflushable(call.Arg<FlatDbColumns>()) ? new UnflushableDb(column) : column;
        });
        db.StartWriteBatch().Returns(_ => _db.StartWriteBatch());
        return db;
    }

    /// <summary>The test database with its snapshots supplied by <paramref name="snapshot"/>.</summary>
    private IColumnsDb<FlatDbColumns> HookedDb(Func<IColumnDbSnapshot<FlatDbColumns>> snapshot)
    {
        IColumnsDb<FlatDbColumns> db = Substitute.For<IColumnsDb<FlatDbColumns>>();
        db.GetColumnDb(Arg.Any<FlatDbColumns>()).Returns(call => _db.GetColumnDb(call.Arg<FlatDbColumns>()));
        db.CreateSnapshot().Returns(_ => snapshot());
        db.CreateSnapshot(Arg.Any<bool>()).Returns(_ => snapshot());
        return db;
    }

    /// <summary>A RocksDB batch whose metadata column rejects every write.</summary>
    private sealed class FailingMetadataBatch(IColumnsWriteBatch<FlatDbColumns> inner) : IColumnsWriteBatch<FlatDbColumns>
    {
        public bool Cleared { get; private set; }
        public bool Disposed { get; private set; }

        public IWriteBatch GetColumnBatch(FlatDbColumns key) => key == FlatDbColumns.Metadata ? new FailingBatch() : inner.GetColumnBatch(key);

        public void Clear()
        {
            Cleared = true;
            inner.Clear();
        }

        public void Dispose()
        {
            Disposed = true;
            inner.Dispose();
        }

        private sealed class FailingBatch : IWriteBatch
        {
            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) => throw new IOException("metadata write failed");
            public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => throw new IOException("metadata write failed");
            public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => throw new IOException("metadata write failed");
            public void Clear() { }
            public void Dispose() { }
        }
    }

    [Test]
    public async Task A_generation_file_cut_short_before_its_header_is_dropped()
    {
        WriteTop(0, 1, Rlp1);
        await _log.DisposeAsync();
        string directory = Path.GetDirectoryName(LogFiles().Single(static file => file.Contains("state-0")))!;
        string stub = Path.Combine(directory, "gen-00000099.log");
        File.WriteAllBytes(stub, Bytes.FromHexString("0x544e"));

        Open();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(stub), Is.False);
            Assert.That(ReadTop(), Is.EqualTo(Rlp1));
        }
    }

    [TestCase(8, 16384)]
    [TestCase(16, 8192)]
    public void Index_capacity_is_the_budget_over_the_ratio_in_slots(int ratio, int slots) =>
        Assert.That(TrieNodeLogGeneration.CapacityFor(1024 * 1024, ratio), Is.EqualTo(slots));

    [Test]
    public void Bypass_batches_may_be_created_from_several_threads_at_once()
    {
        WriteTop(0, 1, Rlp1);
        Assert.That(() => Parallel.For(0, 16, index =>
        {
            using IPersistence.IWriteBatch sync = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
            sync.SetStateTrieNode(TreePath.FromHexString($"{index:x}bcdef"), Rlp2);
        }), Throws.Nothing);
        Assert.That(Raw().LoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1), "the first bypass batch drained the log");
    }

    [Test]
    public void Only_one_log_backed_batch_may_be_open()
    {
        using IPersistence.IWriteBatch open = Batch(0, 1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => _persistence.CreateWriteBatch(State(0), State(1), WriteFlags.None), Throws.InvalidOperationException);
            Assert.That(() => _persistence.Flush(), Throws.InvalidOperationException);
            Assert.That(() => _persistence.Clear(), Throws.InvalidOperationException);
        }
    }
}
