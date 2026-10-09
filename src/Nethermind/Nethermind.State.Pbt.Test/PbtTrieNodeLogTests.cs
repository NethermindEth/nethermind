// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Memory;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Persistence.TrieNodeLog;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

public class PbtTrieNodeLogTests
{
    private static readonly byte[] TopKey = GroupKey(Bytes.FromHexString("0x10"), 4); // TopNodeGroups
    private static readonly byte[] ColdKey = GroupKey(Bytes.FromHexString("0x20"), 4); // TopNodeGroups, same shard as TopKey
    private static readonly byte[] AccountKey = GroupKey(Bytes.FromHexString("0x00123456"), 32); // AccountNodeGroups
    private static readonly byte[] StorageKey = GroupKey(Bytes.FromHexString("0xffaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), 264); // StorageNodeGroups
    private static readonly byte[] Value1 = Bytes.FromHexString("0x1111111111");
    private static readonly byte[] Value2 = Bytes.FromHexString("0x2222222222");
    private static readonly byte[] Value3 = Bytes.FromHexString("0x3333333333");

    private TempPath _directory = null!;
    private SnapshotableMemColumnsDb<PbtColumns> _db = null!;
    private PbtConfig _config = null!;
    private TrieNodeLog _log = null!;

    [SetUp]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        _db = new SnapshotableMemColumnsDb<PbtColumns>();
        // One shard per partition with a 4 KiB generation. The second level is off unless a test enables it, so merges
        // reach RocksDB directly.
        _config = new PbtConfig
        {
            TrieNodeLogEnabled = true,
            TrieNodeLogAccountBytes = 4096,
            TrieNodeLogStorageBytes = 4096,
            TrieNodeLogAccountShardCount = 1,
            TrieNodeLogStorageShardCount = 1,
            TrieNodeLogSecondLevelMergeLag = -1,
        };
        Open();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _log.DisposeAsync();
        _db.Dispose();
        _directory.Dispose();
    }

    private void Open() => _log = new TrieNodeLog(_directory.Path, _db, _config, LimboLogs.Instance);

    private async Task Reopen()
    {
        await _log.DisposeAsync();
        Open();
    }

    private static byte[] GroupKey(byte[] path, int depth) => PbtStorageNodePath.Create(path, depth).ToStorageKey(PbtColumns.StorageNodeGroups);

    private static PbtColumns ColumnOf(byte[] key) => PbtRocksDbPersistence.NodeGroupColumn(PbtNodeGroupKey.Decode(key));

    /// <summary>Commits one log-backed batch the way <see cref="PbtRocksDbPersistence"/> does; a null value deletes.</summary>
    private void Write(params (byte[] Key, byte[]? Value)[] writes)
    {
        IColumnsWriteBatch<PbtColumns> batch = _db.StartWriteBatch();
        using ITrieNodeLog.IWriteBatch logBatch = _log.StartWriteBatch(batch);
        foreach ((byte[] key, byte[]? value) in writes) logBatch.Wrap(ColumnOf(key), batch.GetColumnBatch(ColumnOf(key))).Set(key, value);
        logBatch.Commit();
        batch.Dispose();
        logBatch.Confirm();
    }

    private void WriteTop(byte[] value) => Write((TopKey, value));

    private static byte[]? Read(ITrieNodeLog.IView view, byte[] key) => view.GetColumn(ColumnOf(key)).Get(key);

    private byte[]? Read(byte[] key)
    {
        using ITrieNodeLog.IView view = _log.OpenView(_db);
        return Read(view, key);
    }

    /// <summary>Reads the node-group column directly, bypassing the log.</summary>
    private byte[]? Raw(byte[] key) => _db.GetColumnDb(ColumnOf(key)).Get(key);

    private string[] LogFiles() => Directory.GetFiles(_directory.Path, "*.log", SearchOption.AllDirectories);

    private string[] ShardFiles(string shard) => Directory.GetFiles(Path.Combine(_directory.Path, shard));

    private static byte[] Value(byte seed, int length)
    {
        byte[] value = new byte[length];
        new Random(seed).NextBytes(value);
        return value;
    }

    /// <summary>A 3000-byte value: two fit in a 4 KiB generation, so every second batch seals one.</summary>
    private static byte[] Value(byte seed) => Value(seed, 3000);

    private async Task ReopenAfterTornTailAndLostLastBatch()
    {
        await _log.DisposeAsync();
        foreach (string file in LogFiles()) File.AppendAllText(file, "torn tail garbage");
        RollBackConfirmedVersions();
        Open();
    }

    private async Task MergeAllOnDiskAndReopen()
    {
        await _log.DisposeAsync();
        TrieNodeLog.MergeAllOnDisk(_directory.Path, _db, LimboLogs.Instance);
        Open();
    }

    private static long Counter(NonBlocking.ConcurrentDictionary<string, long> metric, string label) => metric.TryGetValue(label, out long value) ? value : 0;

    private static long FlushedAccountBytes() => Counter(Metrics.PbtTrieNodeLogFlushedBytes, "account");

    private static long SecondLevelStoredAccountBytes() => Counter(Metrics.PbtTrieNodeLogSecondLevelStoredBytes, "account");

    private void RollBackConfirmedVersions()
    {
        foreach (TrieNodeLogShard shard in _log.Shards)
        {
            byte[] version = _db.GetColumnDb(PbtColumns.Metadata).Get(shard.VersionKey)!;
            version[^1]--;
            _db.GetColumnDb(PbtColumns.Metadata).Set(shard.VersionKey, version);
        }
    }

    [Test]
    public void MultiGet_preserves_order_and_uses_captured_log_values_and_tombstones_before_batched_misses()
    {
        byte[] persistedOnlyKey = GroupKey(Bytes.FromHexString("0x40"), 4);
        byte[] missingKey = GroupKey(Bytes.FromHexString("0x50"), 4);
        byte[] persistedTopValue = Value(11);
        byte[] persistedColdValue = Value(12);
        byte[] persistedOnlyValue = Value(13);
        _db.GetColumnDb(PbtColumns.TopNodeGroups).Set(TopKey, persistedTopValue);
        _db.GetColumnDb(PbtColumns.TopNodeGroups).Set(ColdKey, persistedColdValue);
        _db.GetColumnDb(PbtColumns.TopNodeGroups).Set(persistedOnlyKey, persistedOnlyValue);
        Write((TopKey, Value1), (ColdKey, null));

        TrackingBatchStore topColumn = new(keys =>
        {
            byte[]?[] values = new byte[]?[keys.Length];
            for (int index = 0; index < keys.Length; index++)
                if (keys[index].AsSpan().SequenceEqual(persistedOnlyKey)) values[index] = persistedOnlyValue;
            return values;
        });
        using IColumnDbSnapshot<PbtColumns> realSnapshot = _db.CreateSnapshot();
        IColumnDbSnapshot<PbtColumns> snapshot = Substitute.For<IColumnDbSnapshot<PbtColumns>>();
        snapshot.GetColumn(Arg.Any<PbtColumns>()).Returns(call => call.Arg<PbtColumns>() == PbtColumns.Metadata
            ? realSnapshot.GetColumn(PbtColumns.Metadata)
            : topColumn);
        using ITrieNodeLog.IView view = _log.OpenView(HookedDb(() => snapshot));

        Write((TopKey, Value2), (ColdKey, Value3));
        byte[][] keys = [TopKey, ColdKey, missingKey, persistedOnlyKey, TopKey, ColdKey];
        byte[]?[] values = view.GetColumn(PbtColumns.TopNodeGroups).MultiGet(keys, ReadFlags.HintCacheMiss);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(values, Is.EqualTo(new byte[]?[] { Value1, null, null, persistedOnlyValue, Value1, null }));
            Assert.That(topColumn.MultiGetCalls, Is.EqualTo(1));
            Assert.That(topColumn.LastKeys, Is.EqualTo(new byte[][] { missingKey, persistedOnlyKey }));
            Assert.That(topColumn.LastFlags, Is.EqualTo(ReadFlags.HintCacheMiss));
            Assert.That(topColumn.GetCalls, Is.Zero);
        }
    }

    [Test]
    public void MultiGet_does_not_call_the_snapshot_column_when_all_keys_are_log_hits_or_empty()
    {
        WriteTop(Value1);
        TrackingBatchStore topColumn = new(_ => []);
        using IColumnDbSnapshot<PbtColumns> realSnapshot = _db.CreateSnapshot();
        IColumnDbSnapshot<PbtColumns> snapshot = Substitute.For<IColumnDbSnapshot<PbtColumns>>();
        snapshot.GetColumn(Arg.Any<PbtColumns>()).Returns(call => call.Arg<PbtColumns>() == PbtColumns.Metadata
            ? realSnapshot.GetColumn(PbtColumns.Metadata)
            : topColumn);
        using ITrieNodeLog.IView view = _log.OpenView(HookedDb(() => snapshot));

        byte[]?[] hits = view.GetColumn(PbtColumns.TopNodeGroups).MultiGet([TopKey, TopKey]);
        byte[]?[] empty = view.GetColumn(PbtColumns.TopNodeGroups).MultiGet([]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hits, Is.EqualTo(new byte[]?[] { Value1, Value1 }));
            Assert.That(empty, Is.Empty);
            Assert.That(topColumn.MultiGetCalls, Is.Zero);
            Assert.That(topColumn.GetCalls, Is.Zero);
        }
    }

    [Test]
    public void Readers_see_the_version_of_their_snapshot_across_overwrites_and_merges()
    {
        using ITrieNodeLog.IView before = _log.OpenView(_db);
        WriteTop(Value1);
        ITrieNodeLog.IView atV1 = _log.OpenView(_db);
        WriteTop(Value2);
        ITrieNodeLog.IView atV2 = _log.OpenView(_db);

        _log.Drain();
        using ITrieNodeLog.IView afterMerge = _log.OpenView(_db);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Read(before, TopKey), Is.Null);
            Assert.That(Read(atV1, TopKey), Is.EqualTo(Value1));
            Assert.That(Read(atV2, TopKey), Is.EqualTo(Value2));
            Assert.That(Read(afterMerge, TopKey), Is.EqualTo(Value2));
            Assert.That(Raw(TopKey), Is.EqualTo(Value2));
            Assert.That(LogFiles(), Is.Not.Empty, "the merged generation stays on disk while a reader pins it");
        }

        long hitsBefore = Counter(Metrics.PbtTrieNodeLogReads, "hit");
        long chainHitsBefore = Counter(Metrics.PbtTrieNodeLogReads, "chain");
        atV1.Dispose();
        atV2.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Counter(Metrics.PbtTrieNodeLogReads, "hit") - hitsBefore, Is.EqualTo(1), "a view reports its reads when disposed");
            Assert.That(Counter(Metrics.PbtTrieNodeLogReads, "chain") - chainHitsBefore, Is.EqualTo(1), "version 1 is reached through the prev link of version 2");
            Assert.That(LogFiles, Is.Empty.After(5000, 20));
        }
    }

    [Test]
    public void Merge_writes_only_the_latest_record_per_key_and_applies_tombstones()
    {
        _db.GetColumnDb(PbtColumns.AccountNodeGroups).Set(AccountKey, Value1);
        Write((TopKey, Value1));
        Write((TopKey, Value2), (TopKey, Value3), (AccountKey, null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Read(TopKey), Is.EqualTo(Value3));
            Assert.That(Read(AccountKey), Is.Null, "the tombstone hides the RocksDB value");
            Assert.That(Raw(AccountKey), Is.EqualTo(Value1));
        }

        long flushedBefore = FlushedAccountBytes();
        _log.Drain();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw(TopKey), Is.EqualTo(Value3));
            Assert.That(Raw(AccountKey), Is.Null);
            Assert.That(FlushedAccountBytes() - flushedBefore, Is.EqualTo(TopKey.Length + Value3.Length + AccountKey.Length), "the top key with its latest value and the account tombstone key");
        }
    }

    [Test]
    public void Persistence_logs_node_group_columns_writes_the_root_group_directly_drains_before_parallel_staging_and_allows_one_log_backed_batch()
    {
        PbtRocksDbPersistence persistence = new(_db, _config, _log);
        PbtNodePath rootNode = new([], 0);
        PbtNodePath topNode = new(Bytes.FromHexString("0x10"), 5);
        PbtNodePath rootKey = PbtTestPaths.Locate(rootNode).GroupKey;
        PbtNodePath topKey = PbtTestPaths.Locate(topNode).GroupKey;
        StateId state = new(1, TestItem.KeccakA.ValueHash256);
        byte[] topGroup;
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, state, default, WriteFlags.None))
        {
            using RefCountingMemory root = EncodeGroup(rootNode, TestItem.KeccakA.ValueHash256);
            using RefCountingMemory top = EncodeGroup(topNode, TestItem.KeccakB.ValueHash256);
            topGroup = top.GetSpan().ToArray();
            batch.SetNodeGroup(rootKey, root);
            batch.SetNodeGroup(topKey, top);
            batch.Commit();
        }

        using (IPbtPersistence.IReader reader = persistence.CreateReader())
        using (RefCountingMemory? top = reader.GetNodeGroup(topKey))
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(state));
            Assert.That(top?.GetSpan().ToArray(), Is.EqualTo(topGroup));
            Assert.That(Raw(TopKey), Is.Null, "the top group is held by the log");
            Assert.That(_db.GetColumnDb(PbtColumns.Metadata).Get(PbtRocksDbPersistence.RootNodeGroupKey), Is.Not.Null, "the root group bypasses the log");
        }

        Assert.That(() => Parallel.For(0, 16, _ => persistence.CreateStagingWriteBatch(WriteFlags.DisableWAL).Dispose()), Throws.Nothing);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw(TopKey), Is.EqualTo(topGroup), "a staging batch drains the log first");
            Assert.That(LogFiles, Is.Empty.After(5000, 20));
        }

        StateId next = new(2, TestItem.KeccakB.ValueHash256);
        using IPbtPersistence.IWriteBatch open = persistence.CreateWriteBatch(state, next, default, WriteFlags.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => persistence.CreateWriteBatch(state, next, default, WriteFlags.None), Throws.InvalidOperationException);
            Assert.That(persistence.Flush, Throws.InvalidOperationException);
        }
    }

    /// <summary>The group holding a single branch node at <paramref name="node"/>; the node has a prefix, as a prefixless interior branch would be omitted.</summary>
    private static RefCountingMemory EncodeGroup(PbtNodePath node, in ValueHash256 child) =>
        PbtNodeGroupEncoder.EncodeToMemory(PbtTestPaths.Locate(node).GroupKey,
            [new PbtNodeRecord(node.ToPath<PbtStorageNodePath>(), PbtTreeHarness.EncodeBranch(Bytes.FromHexString("0x80"), 1, child, child))], PooledRefCountingMemoryProvider.Instance);

    [Test]
    public void Every_node_group_column_is_logged_and_the_metadata_column_is_not()
    {
        Write((TopKey, Value1), (AccountKey, Value2), (StorageKey, Value3));
        IColumnsWriteBatch<PbtColumns> batch = _db.StartWriteBatch();
        using (ITrieNodeLog.IWriteBatch logBatch = _log.StartWriteBatch(batch))
        {
            IWriteBatch metadata = batch.GetColumnBatch(PbtColumns.Metadata);
            Assert.That(logBatch.Wrap(PbtColumns.Metadata, metadata), Is.SameAs(metadata));
        }
        batch.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Read(TopKey), Is.EqualTo(Value1));
            Assert.That(Read(AccountKey), Is.EqualTo(Value2));
            Assert.That(Read(StorageKey), Is.EqualTo(Value3));
            Assert.That(Raw(TopKey), Is.Null);
            Assert.That(Raw(AccountKey), Is.Null);
            Assert.That(Raw(StorageKey), Is.Null);
            Assert.That(ShardFiles("account-0"), Is.Not.Empty);
            Assert.That(ShardFiles("storage-0"), Is.Not.Empty, "storage groups have their own partition");
        }

        _log.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw(TopKey), Is.EqualTo(Value1));
            Assert.That(Raw(AccountKey), Is.EqualTo(Value2));
            Assert.That(Raw(StorageKey), Is.EqualTo(Value3));
        }
    }

    [Test]
    public async Task Restart_keeps_committed_records_and_drops_the_rest()
    {
        // Larger than a generation's byte budget, so the batch rolls and spans two files, and than 64 KiB.
        byte[] large = Value(7, PbtNodeGroupCodec.MaxPayloadLength);
        WriteTop(Value1);
        Write((TopKey, large), (AccountKey, Value2));
        WriteTop(Value3);

        // A torn tail plus a batch whose RocksDB write never happened: roll the confirmed version back by one.
        await ReopenAfterTornTailAndLostLastBatch();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Read(TopKey), Is.EqualTo(large));
            Assert.That(Read(AccountKey), Is.EqualTo(Value2));
        }

        _log.Drain();
        Assert.That(Raw(TopKey), Is.EqualTo(large));
    }

    [Test]
    public async Task Restart_after_everything_was_merged_continues_above_the_marker()
    {
        WriteTop(Value1);
        _log.Drain();
        Assert.That(LogFiles, Is.Empty.After(5000, 20));

        await Reopen();
        WriteTop(Value2);
        Assert.That(Read(TopKey), Is.EqualTo(Value2));

        await Reopen();
        Assert.That(Read(TopKey), Is.EqualTo(Value2));
    }

    [Test]
    public async Task Merge_lag_skips_keys_rewritten_in_newer_generations()
    {
        _config.TrieNodeLogMergeLag = 1;
        await Reopen();

        Write((TopKey, Value(1)), (ColdKey, Value1));
        WriteTop(Value(2)); // seals generation 1
        WriteTop(Value(3)); // generation 2

        Assert.That(Raw(ColdKey), Is.Null, "a lag of one keeps generation 1 unmerged until generation 2 is sealed");

        WriteTop(Value(4)); // seals generation 2, so generation 1 is merged
        Assert.That(() => Raw(ColdKey), Is.EqualTo(Value1).After(5000, 20));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw(TopKey), Is.Null, "rewritten in generation 2, so not merged from generation 1");
            Assert.That(Read(TopKey), Is.EqualTo(Value(4)));
        }
        Assert.That(LogFiles, Has.Length.EqualTo(1).After(5000, 20), "generation 1 deleted, generation 2 sealed and waiting for a newer one");

        _log.Drain();
        Assert.That(Raw(TopKey), Is.EqualTo(Value(4)));
    }

    private async Task ReopenWithSecondLevel(int secondLevelMergeLag)
    {
        _config.TrieNodeLogMergeLag = 0;
        _config.TrieNodeLogSecondLevelMergeLag = secondLevelMergeLag;
        await Reopen();
    }

    [Test]
    public async Task Second_level_takes_merged_generations_and_merges_its_own_full_ones_into_RocksDB()
    {
        await ReopenWithSecondLevel(secondLevelMergeLag: 0);

        Write((TopKey, Value(1)), (ColdKey, Value1));
        long secondLevelStoredBefore = SecondLevelStoredAccountBytes();
        WriteTop(Value(2)); // seals first-level generation 1, copied into the second level
        Assert.That(() => ShardFiles("account-0"), Is.Empty.After(5000, 20));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ShardFiles("account-0-l2"), Is.Not.Empty);
            Assert.That(SecondLevelStoredAccountBytes() - secondLevelStoredBefore, Is.EqualTo(TrieNodeLogRecord.HeaderLength * 3 + TopKey.Length + 3000 + ColdKey.Length + Value1.Length),
                "the latest top record and the cold record, plus a commit record");
            Assert.That(Raw(TopKey), Is.Null);
            Assert.That(Raw(ColdKey), Is.Null);
            Assert.That(Read(TopKey), Is.EqualTo(Value(2)));
        }

        using ITrieNodeLog.IView beforeAccount = _log.OpenView(_db);
        Assert.That(beforeAccount.GetColumn(PbtColumns.TopNodeGroups).MultiGet([ColdKey, TopKey, ColdKey]),
            Is.EqualTo(new byte[]?[] { Value1, Value(2), Value1 }));
        Write((AccountKey, Value2), (TopKey, Value(3)));
        WriteTop(Value(4)); // seals first-level generation 2, whose copy fills the second level's generation
        Assert.That(() => Raw(TopKey), Is.EqualTo(Value(4)).After(5000, 20));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw(ColdKey), Is.EqualTo(Value1));
            Assert.That(Raw(AccountKey), Is.EqualTo(Value2));
            Assert.That(Read(beforeAccount, AccountKey), Is.Null, "the second-level copy is newer than the reader's version");
            Assert.That(Read(beforeAccount, TopKey), Is.EqualTo(Value(2)));
            Assert.That(Read(beforeAccount, ColdKey), Is.EqualTo(Value1));
        }

        beforeAccount.Dispose();
        Assert.That(LogFiles, Is.Empty.After(5000, 20));

        // The first level is empty and only the second level holds the latest value when the drain starts.
        WriteTop(Value(5));
        WriteTop(Value(6));
        Assert.That(() => ShardFiles("account-0"), Is.Empty.After(5000, 20));
        _log.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw(TopKey), Is.EqualTo(Value(6)));
            Assert.That(LogFiles, Is.Empty.After(5000, 20));
        }
    }

    [Test]
    public async Task Second_level_merge_lag_is_configured_separately()
    {
        await ReopenWithSecondLevel(secondLevelMergeLag: 1);

        // Every fourth batch seals a second-level generation.
        Write((TopKey, Value(1)), (ColdKey, Value1));
        for (byte block = 1; block < 4; block++) WriteTop(Value((byte)(block + 1)));
        Assert.That(() => ShardFiles("account-0"), Is.Empty.After(5000, 20), "a first-level lag of zero merges every sealed generation");
        Assert.That(Raw(ColdKey), Is.Null, "a second-level lag of one keeps second-level generation 1 until generation 2 is sealed");

        for (byte block = 4; block < 8; block++) WriteTop(Value((byte)(block + 1)));
        Assert.That(() => Raw(ColdKey), Is.EqualTo(Value1).After(5000, 20));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw(TopKey), Is.Null, "rewritten in second-level generation 2, so not merged from generation 1");
            Assert.That(Read(TopKey), Is.EqualTo(Value(8)));
        }
    }

    [Test]
    public async Task Second_level_survives_a_restart_and_the_startup_merge_takes_it_first()
    {
        await ReopenWithSecondLevel(secondLevelMergeLag: 0);
        byte[] large = Value(1);

        Write((TopKey, large), (AccountKey, Value1));
        WriteTop(large); // seals first-level generation 1, copied into the second level
        Assert.That(() => ShardFiles("account-0"), Is.Empty.After(5000, 20));
        WriteTop(Value3);

        // A torn tail plus a batch whose RocksDB write never happened: the second level is confirmed by the first
        // level's version, so it survives the rollback that drops the last first-level batch.
        await ReopenAfterTornTailAndLostLastBatch();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Read(TopKey), Is.EqualTo(large));
            Assert.That(Read(AccountKey), Is.EqualTo(Value1));
        }

        WriteTop(Value3);
        await MergeAllOnDiskAndReopen();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LogFiles(), Is.Empty);
            Assert.That(Raw(TopKey), Is.EqualTo(Value3), "the first level's newer record is merged after the second level's");
            Assert.That(Raw(AccountKey), Is.EqualTo(Value1));
        }
    }

    [Test]
    public async Task Keys_are_sharded_by_their_hash_and_the_startup_merge_takes_any_layout()
    {
        _config.TrieNodeLogAccountShardCount = 2;
        await Reopen();
        byte[][] keys = [.. Enumerable.Range(0, 16).Select(static index => GroupKey([(byte)(index << 4)], 4))];
        Write([.. keys.Select(static key => (key, (byte[]?)key))]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(keys.Select(key => _log.ShardIndex(PbtColumns.TopNodeGroups, key)).Distinct().Count(), Is.EqualTo(2));
            Assert.That(ShardFiles("account-0"), Is.Not.Empty);
            Assert.That(ShardFiles("account-1"), Is.Not.Empty);
            foreach (byte[] key in keys) Assert.That(Read(key), Is.EqualTo(key));
        }

        // A different shard count needs the directory merged first; then reads come from RocksDB.
        _config.TrieNodeLogAccountShardCount = 1;
        await MergeAllOnDiskAndReopen();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Directory.GetDirectories(_directory.Path).Where(static directory => Directory.GetFiles(directory).Length > 0), Is.Empty);
            foreach (byte[] key in keys) Assert.That(Raw(key), Is.EqualTo(key));
            foreach (byte[] key in keys) Assert.That(Read(key), Is.EqualTo(key));
        }
    }

    [Test]
    public async Task Persistence_backs_up_on_the_merge_backlog_and_completes()
    {
        // One merge at a time and no slack beyond the lag: every other batch rolls, so rolls regularly have to wait.
        _config.TrieNodeLogMaxConcurrentMerges = 1;
        _config.TrieNodeLogMergeBacklogMargin = 1;
        await Reopen();

        byte[] value = [];
        for (byte block = 0; block < 24; block++)
        {
            value = Value(block);
            WriteTop(value);
            Assert.That(Read(TopKey), Is.EqualTo(value));
        }

        _log.Drain();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw(TopKey), Is.EqualTo(value));
            Assert.That(LogFiles, Is.Empty.After(5000, 20));
        }
    }

    [TestCase(1, -1, -1, true)]
    [TestCase(2, -1, -1, false)]
    [TestCase(1, 0, 0, true)]
    [TestCase(1, 0, -1, false)]
    [TestCase(1, -1, 0, true)]
    public async Task The_on_disk_layout_matches_the_config_only_with_the_same_shards(int accountShardCount, int writtenSecondLevelMergeLag, int secondLevelMergeLag, bool matches)
    {
        if (writtenSecondLevelMergeLag >= 0) await ReopenWithSecondLevel(writtenSecondLevelMergeLag);
        WriteTop(Value1);
        await _log.DisposeAsync();
        _config.TrieNodeLogAccountShardCount = accountShardCount;
        _config.TrieNodeLogSecondLevelMergeLag = secondLevelMergeLag;

        Assert.That(TrieNodeLog.MatchesOnDiskLayout(_directory.Path, _config), Is.EqualTo(matches));
        Directory.CreateDirectory(Path.Combine(_directory.Path, "unknown-0"));
        Assert.That(TrieNodeLog.MatchesOnDiskLayout(_directory.Path, _config), Is.False, "a directory of another layout never matches");
    }

    [Test]
    public async Task Create_merges_the_log_of_a_disabled_config_and_returns_the_null_log()
    {
        using TempPath baseDirectory = TempPath.GetTempDirectory();
        await _log.DisposeAsync();
        _log = new TrieNodeLog(Path.Combine(baseDirectory.Path, "pbtTrieNodeLog"), _db, _config, LimboLogs.Instance);
        WriteTop(Value1);
        await _log.DisposeAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(TrieNodeLog.Create(new PbtConfig(), new InitConfig { BaseDbPath = baseDirectory.Path }, _db, LimboLogs.Instance), Is.SameAs(NullTrieNodeLog.Instance));
            Assert.That(Raw(TopKey), Is.EqualTo(Value1));
            Assert.That(Directory.GetFiles(baseDirectory.Path, "*.log", SearchOption.AllDirectories), Is.Empty);
        }
    }

    [Test]
    public async Task Draining_on_shutdown_is_opt_in()
    {
        WriteTop(Value1);
        await _log.DisposeAsync();
        Assert.That(Raw(TopKey), Is.Null, "by default the log is kept for the next start");

        _config.TrieNodeLogDrainOnShutdown = true;
        Open();
        await _log.DisposeAsync();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw(TopKey), Is.EqualTo(Value1));
            Assert.That(LogFiles(), Is.Empty);
        }
    }

    [Test]
    public async Task A_generation_file_of_another_format_is_refused()
    {
        WriteTop(Value1);
        await _log.DisposeAsync();
        string file = LogFiles().Single(static file => file.Contains("account-0"));
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
        using (IColumnsWriteBatch<PbtColumns> rocksDbBatch = _db.StartWriteBatch())
        using (ITrieNodeLog.IWriteBatch logBatch = _log.StartWriteBatch(rocksDbBatch))
        {
            logBatch.Wrap(PbtColumns.TopNodeGroups, rocksDbBatch.GetColumnBatch(PbtColumns.TopNodeGroups)).PutSpan(TopKey, Value1);
            logBatch.Commit();
            rocksDbBatch.Clear(); // the RocksDB write "failed": nothing of this batch, its version included, reaches RocksDB
        }

        Assert.That(() => WriteTop(Value2), Throws.InvalidOperationException.With.Message.Contains("restart"));

        await Reopen();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Read(TopKey), Is.Null);
            Assert.That(() => WriteTop(Value2), Throws.Nothing);
        }
    }

    [Test]
    public void A_commit_failing_after_the_log_is_durable_poisons_the_log()
    {
        using (IColumnsWriteBatch<PbtColumns> rocksDbBatch = _db.StartWriteBatch())
        using (ITrieNodeLog.IWriteBatch logBatch = _log.StartWriteBatch(new FailingMetadataBatch(rocksDbBatch)))
        {
            logBatch.Wrap(PbtColumns.TopNodeGroups, rocksDbBatch.GetColumnBatch(PbtColumns.TopNodeGroups)).PutSpan(TopKey, Value1);
            Assert.That(logBatch.Commit, Throws.TypeOf<IOException>(), "the version write into the RocksDB batch fails after the log records are fsynced");
        }

        Assert.That(() => WriteTop(Value2), Throws.InvalidOperationException.With.Message.Contains("restart"));
    }

    [Test]
    public void A_generation_committed_before_the_snapshot_and_merged_right_after_stays_readable()
    {
        // Between a reader pinning the live generations and binding to its snapshot: a batch commits into a new
        // generation, the snapshot is taken, and a drain merges and deletes that generation.
        Task drain = Task.CompletedTask;
        IColumnsDb<PbtColumns> db = HookedDb(() =>
        {
            WriteTop(Value1);
            IColumnDbSnapshot<PbtColumns> snapshot = _db.CreateSnapshot();
            drain = Task.Run(() => _log.Drain());
            SpinWait.SpinUntil(() => drain.IsCompleted, TimeSpan.FromMilliseconds(500));
            return snapshot;
        });

        using (ITrieNodeLog.IView view = _log.OpenView(db))
        {
            Assert.That(Read(view, TopKey), Is.EqualTo(Value1));
        }
        Assert.That(() => drain.Wait(TimeSpan.FromSeconds(5)), Throws.Nothing);
    }

    [Test]
    public void A_view_that_fails_to_bind_releases_its_generations()
    {
        WriteTop(Value1);
        IColumnDbSnapshot<PbtColumns> snapshot = Substitute.For<IColumnDbSnapshot<PbtColumns>>();
        snapshot.GetColumn(Arg.Any<PbtColumns>()).Returns(static _ => throw new IOException("metadata read failed"));

        Assert.That(() => _log.OpenView(HookedDb(() => snapshot)), Throws.TypeOf<IOException>());
        _log.Drain();
        Assert.That(LogFiles, Is.Empty.After(5000, 20), "a leaked lease would keep the merged file");
    }

    [Test]
    public void A_failed_log_commit_releases_the_RocksDB_batch()
    {
        FailingMetadataBatch? batch = null;
        IColumnsDb<PbtColumns> db = HookedDb(() => _db.CreateSnapshot());
        db.ColumnKeys.Returns(_db.ColumnKeys);
        db.StartWriteBatch().Returns(_ => batch = new FailingMetadataBatch(_db.StartWriteBatch()));
        PbtRocksDbPersistence persistence = new(db, _config, _log);

        IPbtPersistence.IWriteBatch writeBatch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, TestItem.KeccakA.ValueHash256), default, WriteFlags.None);
        PbtNodePath topNode = new(Bytes.FromHexString("0x10"), 5);
        using (RefCountingMemory top = EncodeGroup(topNode, TestItem.KeccakB.ValueHash256))
            writeBatch.SetNodeGroup(PbtTestPaths.Locate(topNode).GroupKey, top);
        Assert.That(writeBatch.Commit, Throws.TypeOf<IOException>());
        writeBatch.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch!.Cleared, Is.True);
            Assert.That(batch.Disposed, Is.True);
        }
    }

    [Test]
    public async Task A_database_restored_without_its_log_directory_is_refused()
    {
        WriteTop(Value1);
        await _log.DisposeAsync();
        foreach (string file in LogFiles()) File.Delete(file);

        Assert.That(Open, Throws.TypeOf<InvalidDataException>().With.Message.Contains("missing"));
    }

    [Test]
    public async Task A_generation_file_cut_short_before_its_header_is_dropped()
    {
        WriteTop(Value1);
        await _log.DisposeAsync();
        string stub = Path.Combine(_directory.Path, "account-0", "gen-00000099.log");
        File.WriteAllBytes(stub, Bytes.FromHexString("0x544e"));

        Open();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(File.Exists(stub), Is.False);
            Assert.That(Read(TopKey), Is.EqualTo(Value1));
        }
    }

    [TestCase(8, 16384)]
    [TestCase(16, 8192)]
    public void Index_capacity_is_the_budget_over_the_ratio_in_slots(int ratio, int slots) =>
        Assert.That(TrieNodeLogGeneration.CapacityFor(1024 * 1024, ratio), Is.EqualTo(slots));

    private sealed class TrackingBatchStore(Func<byte[][], byte[]?[]> multiGet) : IReadOnlyKeyValueStore
    {
        public int MultiGetCalls { get; private set; }
        public int GetCalls { get; private set; }
        public byte[][]? LastKeys { get; private set; }
        public ReadFlags LastFlags { get; private set; }

        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
        {
            GetCalls++;
            return null;
        }

        public byte[]?[] MultiGet(byte[][] keys, ReadFlags flags = ReadFlags.None)
        {
            MultiGetCalls++;
            LastKeys = keys;
            LastFlags = flags;
            return multiGet(keys);
        }
    }

    /// <summary>The test database with its snapshots supplied by <paramref name="snapshot"/>.</summary>
    private IColumnsDb<PbtColumns> HookedDb(Func<IColumnDbSnapshot<PbtColumns>> snapshot)
    {
        IColumnsDb<PbtColumns> db = Substitute.For<IColumnsDb<PbtColumns>>();
        db.GetColumnDb(Arg.Any<PbtColumns>()).Returns(call => _db.GetColumnDb(call.Arg<PbtColumns>()));
        db.CreateSnapshot().Returns(_ => snapshot());
        return db;
    }

    /// <summary>A RocksDB batch whose metadata column rejects every write.</summary>
    private sealed class FailingMetadataBatch(IColumnsWriteBatch<PbtColumns> inner) : IColumnsWriteBatch<PbtColumns>
    {
        public bool Cleared { get; private set; }
        public bool Disposed { get; private set; }

        public IWriteBatch GetColumnBatch(PbtColumns key) => key == PbtColumns.Metadata ? new FailingBatch() : inner.GetColumnBatch(key);

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
}
