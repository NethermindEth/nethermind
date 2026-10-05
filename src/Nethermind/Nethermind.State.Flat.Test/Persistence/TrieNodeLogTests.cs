// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
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

namespace Nethermind.State.Flat.Test.Persistence;

public class TrieNodeLogTests
{
    private static readonly TreePath TopPath = TreePath.FromHexString("12345"); // StateTopNodes
    private static readonly TreePath MediumPath = TreePath.FromHexString("123456789abc"); // StateNodes
    private static readonly TreePath StoragePath = TreePath.FromHexString("abcd"); // StorageNodes
    private static readonly byte[] Rlp1 = Bytes.FromHexString("0x1111111111");
    private static readonly byte[] Rlp2 = Bytes.FromHexString("0x2222222222");
    private static readonly byte[] Rlp3 = Bytes.FromHexString("0x3333333333");

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
        // Two shards per partition, so every shard gets a 4 KiB generation.
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
        _log = new TrieNodeLog(_directory.Path, _db, _config, LimboLogs.Instance);
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
        return reader.TryLoadStateRlp(TopPath, ReadFlags.None);
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
            Assert.That(before.TryLoadStateRlp(TopPath, ReadFlags.None), Is.Null);
            Assert.That(atV1.TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(atV2.TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp2));
            Assert.That(afterMerge.TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp2));
            Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp2));
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
            Assert.That(reader.TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp3));
            Assert.That(reader.TryLoadStateRlp(MediumPath, ReadFlags.None), Is.Null, "tombstone hides the RocksDB value");
            Assert.That(Raw().TryLoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp1));
        }

        long flushedBefore = FlushedStateBytes();
        _log.Drain();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp3));
            Assert.That(Raw().TryLoadStateRlp(MediumPath, ReadFlags.None), Is.Null);
            Assert.That(FlushedStateBytes() - flushedBefore, Is.EqualTo(3 + Rlp3.Length + 8), "one 3-byte top key with its latest value and one 8-byte tombstone key");
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
            Assert.That(reader.TryLoadStateRlp(longPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(Raw().TryLoadStateRlp(longPath, ReadFlags.None), Is.EqualTo(Rlp1), "fallback nodes bypass the log");
            Assert.That(reader.TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(reader.TryLoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp2));
            Assert.That(reader.TryLoadStorageRlp(address, StoragePath, ReadFlags.None), Is.EqualTo(Rlp3));
            Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.Null);
            Assert.That(Raw().TryLoadStateRlp(MediumPath, ReadFlags.None), Is.Null);
            Assert.That(Raw().TryLoadStorageRlp(address, StoragePath, ReadFlags.None), Is.Null);
        }

        _log.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(Raw().TryLoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp2));
            Assert.That(Raw().TryLoadStorageRlp(address, StoragePath, ReadFlags.None), Is.EqualTo(Rlp3));
        }
    }

    [Test]
    public void Sync_batch_drains_the_log_and_writes_directly()
    {
        WriteTop(0, 1, Rlp1);
        Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.Null);

        using (IPersistence.IWriteBatch sync = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL))
        {
            sync.SetStateTrieNode(MediumPath, Rlp2);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(Raw().TryLoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp2));
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
            Assert.That(reader.TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(large));
            Assert.That(reader.TryLoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp2));
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
        _config.TrieNodeLogMergeLag = 1;
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

        Assert.That(Raw().TryLoadStateRlp(coldPath, ReadFlags.None), Is.Null, "a lag of one keeps generation 1 unmerged until generation 2 is sealed");

        WriteTop(3, 4, Value(4)); // seals generation 2, so generation 1 is merged
        Assert.That(() => Raw().TryLoadStateRlp(coldPath, ReadFlags.None), Is.EqualTo(Rlp1).After(5000, 20));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.Null, "rewritten in generation 2, so not merged from generation 1");
            Assert.That(ReadTop(), Is.EqualTo(Value(4)));
        }
        Assert.That(LogFiles, Has.Length.EqualTo(1).After(5000, 20), "generation 1 deleted, generation 2 sealed and waiting for a newer one");

        _log.Drain();
        Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Value(4)));
    }

    private async Task ReopenWithSecondLevel(int secondLevelMergeLag)
    {
        _config.TrieNodeLogSecondLevelEnabled = true;
        _config.TrieNodeLogMergeLag = 0;
        _config.TrieNodeLogSecondLevelMergeLag = secondLevelMergeLag;
        await Reopen();
    }

    private string[] ShardFiles(string shard) => Directory.GetFiles(Path.Combine(_directory.Path, shard));

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
            Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.Null);
            Assert.That(Raw().TryLoadStateRlp(coldPath, ReadFlags.None), Is.Null);
            Assert.That(ReadTop(), Is.EqualTo(Value(2)));
        }

        using IPersistence.IPersistenceReader beforeMedium = _persistence.CreateReader();
        using (IPersistence.IWriteBatch batch = Batch(2, 3))
        {
            batch.SetStateTrieNode(MediumPath, Rlp2);
            batch.SetStateTrieNode(TopPath, Value(3));
        }
        WriteTop(3, 4, Value(4)); // seals first-level generation 2, whose copy fills the second level's generation
        Assert.That(() => Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Value(4)).After(5000, 20));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().TryLoadStateRlp(coldPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(Raw().TryLoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp2));
            Assert.That(beforeMedium.TryLoadStateRlp(MediumPath, ReadFlags.None), Is.Null, "the second-level copy is newer than the reader's version");
            Assert.That(beforeMedium.TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Value(2)));
            Assert.That(beforeMedium.TryLoadStateRlp(coldPath, ReadFlags.None), Is.EqualTo(Rlp1));
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
            Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Value(6)));
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
        Assert.That(Raw().TryLoadStateRlp(coldPath, ReadFlags.None), Is.Null, "a second-level lag of one keeps second-level generation 1 until generation 2 is sealed");

        for (ulong block = 4; block < 8; block++) WriteTop(block, block + 1, Value((byte)(block + 1)));
        Assert.That(() => Raw().TryLoadStateRlp(coldPath, ReadFlags.None), Is.EqualTo(Rlp1).After(5000, 20));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.Null, "rewritten in second-level generation 2, so not merged from generation 1");
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
            Assert.That(reader.TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(large));
            Assert.That(reader.TryLoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp1));
        }

        WriteTop(3, 4, Rlp3);
        await _log.DisposeAsync();
        TrieNodeLog.MergeAllOnDisk(_directory.Path, _db, LimboLogs.Instance);
        Open();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LogFiles(), Is.Empty);
            Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp3), "the first level's newer record is merged after the second level's");
            Assert.That(Raw().TryLoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp1));
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
            Assert.That(reader.TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(reader.TryLoadStateRlp(highPath, ReadFlags.None), Is.EqualTo(Rlp2));
        }

        _log.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(Raw().TryLoadStateRlp(highPath, ReadFlags.None), Is.EqualTo(Rlp2));
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
            Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(value));
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
        Assert.That(Raw().TryLoadStateRlp(highPath, ReadFlags.None), Is.Null);

        TrieNodeLog.MergeAllOnDisk(_directory.Path, _db, LimboLogs.Instance);

        // The directory is clean, so a different shard count starts from an empty log and reads come from RocksDB.
        _config.TrieNodeLogStateShardCount = 1;
        Open();
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.GetDirectories(_directory.Path).Where(static directory => Directory.GetFiles(directory).Length > 0), Is.Empty);
            Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(Raw().TryLoadStateRlp(highPath, ReadFlags.None), Is.EqualTo(Rlp2));
            Assert.That(Raw().TryLoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp3));
            Assert.That(reader.TryLoadStateRlp(highPath, ReadFlags.None), Is.EqualTo(Rlp2));
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

        Assert.That(TrieNodeLog.MatchesOnDiskLayout(_directory.Path, _config), Is.EqualTo(matches));
    }

    [TestCase(true, true, true)]
    [TestCase(true, false, false)]
    [TestCase(false, true, true)]
    [TestCase(false, false, true)]
    public async Task The_on_disk_second_level_matches_the_config_only_when_enabled(bool writtenWithSecondLevel, bool secondLevelEnabled, bool matches)
    {
        if (writtenWithSecondLevel) await ReopenWithSecondLevel(secondLevelMergeLag: 0);
        WriteTop(0, 1, Rlp1);
        await _log.DisposeAsync();
        _config.TrieNodeLogSecondLevelEnabled = secondLevelEnabled;

        Assert.That(TrieNodeLog.MatchesOnDiskLayout(_directory.Path, _config), Is.EqualTo(matches));
    }

    [Test]
    public async Task The_on_disk_layout_does_not_match_with_a_directory_of_another_layout()
    {
        WriteTop(0, 1, Rlp1);
        await _log.DisposeAsync();
        Directory.CreateDirectory(Path.Combine(_directory.Path, "state_top-0"));

        Assert.That(TrieNodeLog.MatchesOnDiskLayout(_directory.Path, _config), Is.False);
    }

    [Test]
    public async Task Draining_on_shutdown_is_opt_in()
    {
        WriteTop(0, 1, Rlp1);
        await _log.DisposeAsync();
        Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.Null, "by default the log is kept for the next start");

        _config.TrieNodeLogDrainOnShutdown = true;
        Open();
        await _log.DisposeAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1));
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
            Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.Null, "the drain preceded the snapshot the range delete scanned");
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
        Assert.That(() => drain.Wait(TimeSpan.FromSeconds(5)), Throws.Nothing);
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
    public async Task A_database_restored_without_its_log_directory_is_refused()
    {
        WriteTop(0, 1, Rlp1);
        await _log.DisposeAsync();
        foreach (string file in LogFiles()) File.Delete(file);

        Assert.That(Open, Throws.TypeOf<InvalidDataException>().With.Message.Contains("missing"));
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
        Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1), "the first bypass batch drained the log");
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
