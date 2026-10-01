// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
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
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture(false)]
[TestFixture(true)]
public class TrieNodeLogTests(bool compression)
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
        _config = new FlatDbConfig { TrieNodeLogScope = TrieNodeLogScope.All, TrieNodeLogStateTopBytes = 8192, TrieNodeLogStateBytes = 8192, TrieNodeLogStorageBytes = 8192, TrieNodeLogCompression = compression };
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

    /// <summary>Random, hence incompressible, bytes so generations fill by bytes with compression on as well.</summary>
    private static byte[] Value(byte seed, int length)
    {
        byte[] value = new byte[length];
        new Random(seed).NextBytes(value);
        return value;
    }

    private static long FlushedBytes(FlatDbColumns column) =>
        Metrics.TrieNodeLogFlushedBytes.TryGetValue(TrieNodeLogLabel.Column((byte)column), out long bytes) ? bytes : 0;

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

        long topBefore = FlushedBytes(FlatDbColumns.StateTopNodes);
        long stateBefore = FlushedBytes(FlatDbColumns.StateNodes);
        _log.Drain();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp3));
            Assert.That(Raw().TryLoadStateRlp(MediumPath, ReadFlags.None), Is.Null);
            Assert.That(FlushedBytes(FlatDbColumns.StateTopNodes) - topBefore, Is.EqualTo(3 + Rlp3.Length), "one 3-byte top key with its latest value");
            Assert.That(FlushedBytes(FlatDbColumns.StateNodes) - stateBefore, Is.EqualTo(8), "one 8-byte tombstone key");
        }
    }

    [Test]
    public void Scope_selects_the_logged_columns([Values(TrieNodeLogScope.StateTop, TrieNodeLogScope.State, TrieNodeLogScope.All)] TrieNodeLogScope scope)
    {
        _config.TrieNodeLogScope = scope;
        Reopen().GetAwaiter().GetResult();

        Hash256 address = TestItem.KeccakA;
        using (IPersistence.IWriteBatch batch = Batch(0, 1))
        {
            batch.SetStateTrieNode(TopPath, Rlp1);
            batch.SetStateTrieNode(MediumPath, Rlp2);
            batch.SetStorageTrieNode(address, StoragePath, Rlp3);
        }

        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp1));
            Assert.That(reader.TryLoadStateRlp(MediumPath, ReadFlags.None), Is.EqualTo(Rlp2));
            Assert.That(reader.TryLoadStorageRlp(address, StoragePath, ReadFlags.None), Is.EqualTo(Rlp3));
            Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.Null);
            Assert.That(Raw().TryLoadStateRlp(MediumPath, ReadFlags.None), scope >= TrieNodeLogScope.State ? Is.Null : Is.EqualTo(Rlp2));
            Assert.That(Raw().TryLoadStorageRlp(address, StoragePath, ReadFlags.None), scope == TrieNodeLogScope.All ? Is.Null : Is.EqualTo(Rlp3));
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
            Assert.That(Directory.GetFiles(Path.Combine(_directory.Path, "state_top-0")), Is.Not.Empty);
            Assert.That(Directory.GetFiles(Path.Combine(_directory.Path, "state_top-1")), Is.Not.Empty);
            Assert.That(Directory.GetFiles(Path.Combine(_directory.Path, "state-0")), Is.Empty, "StateNodes keys have their own partition");
            Assert.That(Directory.Exists(Path.Combine(_directory.Path, "state-1")), Is.False, "the state partition has one shard by default");
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
