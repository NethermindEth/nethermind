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
        _config = new FlatDbConfig { TrieNodeLogScope = TrieNodeLogScope.All, TrieNodeLogGenerationBytes = 4096 };
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

    private string[] LogFiles() => Directory.GetFiles(_directory.Path);

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

        long flushedBefore = Metrics.TrieNodeLogFlushedBytes;
        _log.Drain();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Raw().TryLoadStateRlp(TopPath, ReadFlags.None), Is.EqualTo(Rlp3));
            Assert.That(Raw().TryLoadStateRlp(MediumPath, ReadFlags.None), Is.Null);
            Assert.That(Metrics.TrieNodeLogFlushedBytes - flushedBefore, Is.EqualTo(3 + Rlp3.Length + 8), "one 3-byte top key with its latest value and one 8-byte tombstone key");
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
        byte[] large = new byte[5000];
        Array.Fill(large, (byte)7);
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
        byte[] version = _db.GetColumnDb(FlatDbColumns.Metadata).Get(TrieNodeLog.VersionKey)!;
        version[^1]--;
        _db.GetColumnDb(FlatDbColumns.Metadata).Set(TrieNodeLog.VersionKey, version);
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
