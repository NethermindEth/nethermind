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
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.PaprikaFlat;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.Trie;
using NUnit.Framework;
using Paprika;
using Paprika.Store;
using Paprika.Store.PageManagers;
using Testably.Abstractions;

namespace Nethermind.State.Flat.Test.Persistence.PaprikaFlat;

public class PaprikaFlatPersistenceTests
{
    private const long FileSize = 64 * 1024 * 1024;
    private static readonly TimeSpan ReaderWait = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly TreePath TopPath = TreePath.FromHexString("12345");
    private static readonly Address Owner = TestItem.AddressA;
    private static readonly IFileSystem FileSystem = new RealFileSystem();

    private TempPath _directory = null!;
    private SnapshotableMemColumnsDb<FlatDbColumns> _rocksDb = null!;
    private FaultyColumnsDb _columns = null!;
    private FlatDbConfig _config = null!;
    private FaultyPageManager _pages = null!;
    private TimeSpan _readerWait;
    private PaprikaFlatStore _store = null!;
    private TrieNodeLog _log = null!;
    private PaprikaFlatPersistence _persistence = null!;

    private string LogDirectory => Path.Combine(_directory.Path, "flatTrieNodeLog");
    private string PaprikaDirectory => Path.Combine(_directory.Path, "flatPaprika");
    private string PaprikaFile => MemoryMappedPageManager.GetPaprikaFilePath(PaprikaDirectory);

    [SetUp]
    public void Setup()
    {
        _directory = TempPath.GetTempDirectory();
        _rocksDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _columns = new FaultyColumnsDb(_rocksDb);
        // Two shards per partition, so every shard gets a 4 KiB generation and merges run while blocks are written.
        _config = new FlatDbConfig { Layout = FlatLayout.Flat, TrieNodeLogEnabled = true, TrieNodeLogStateBytes = 8192, TrieNodeLogStorageBytes = 8192 };
        _readerWait = ReaderWait;
        Open();
    }

    [TearDown]
    public async Task TearDown()
    {
        await Close();
        _columns.Dispose();
        _rocksDb.Dispose();
        _directory.Dispose();
    }

    /// <summary>Builds the layout as the DI container does: the store, then the log recovering against it, then the persistence.</summary>
    private void Open()
    {
        _store = new PaprikaFlatStore(_columns, PaprikaDirectory, OpenPaprika, _readerWait, LimboLogs.Instance);
        _log = new TrieNodeLog(LogDirectory, FileSystem, _columns, _store, _config, LimboLogs.Instance);
        _persistence = new PaprikaFlatPersistence(_columns, _store, _log, _config);
    }

    private PagedDb OpenPaprika() =>
        new(_pages = new FaultyPageManager(new MemoryMappedPageManager(FileSize, PaprikaFlatStore.HistoryDepth, PaprikaDirectory, accessObserver: null)), PaprikaFlatStore.HistoryDepth);

    private async Task Close()
    {
        await _log.DisposeAsync();
        _store.Dispose();
    }

    /// <summary>Closes and reopens over the same files and flat DB, as a restart after a crash.</summary>
    private async Task Reopen()
    {
        await Close();
        Open();
    }

    private static StateId State(ulong number) => number == 0 ? StateId.PreGenesis : new StateId(number, ValueKeccak.Compute($"state{number}"));

    /// <summary>What a reader at block <paramref name="number"/> holds: see <see cref="ReadBlock"/>.</summary>
    private static (StateId State, ulong? Nonce, ulong? TopNode) Block(ulong number) => number == 0 ? (StateId.PreGenesis, null, null) : (State(number), number, number);

    /// <summary>Persists block <paramref name="number"/>: the owner's nonce and the top trie node both hold the number.</summary>
    private static void WriteBlock(IPersistence persistence, ulong number)
    {
        // Padded, so a few dozen blocks fill a generation of the log.
        byte[] topNode = new byte[128];
        BinaryPrimitives.WriteUInt64BigEndian(topNode, number);
        using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(State(number - 1), State(number), WriteFlags.None);
        batch.SetAccount(Owner, new Account(number, 1));
        batch.SetStateTrieNode(TopPath, topNode);
        // A node of this block alone, which a merge writes on: the top node's older records it skips.
        batch.SetStateTrieNode(TreePath.FromHexString($"12345{number:x8}"), topNode);
    }

    /// <summary>The state pointer, the owner's nonce and the number in the top trie node, which agree when the reader is bound to one commit.</summary>
    private static (StateId State, ulong? Nonce, ulong? TopNode) ReadBlock(IPersistence.IPersistenceReader reader) =>
        (reader.CurrentState,
            reader.GetAccount(Owner) is { } account ? (ulong)account.Nonce : null,
            reader.TryLoadStateRlp(TopPath, ReadFlags.None) is { } node ? BinaryPrimitives.ReadUInt64BigEndian(node) : null);

    private static (StateId State, ulong? Nonce, ulong? TopNode) ReadLatestBlock(IPersistence persistence)
    {
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        return ReadBlock(reader);
    }

    /// <summary>Commits a sync batch writing an account, one of its slots and a state trie node, all keyed by <paramref name="index"/>.</summary>
    private void WriteSyncBatch(int index)
    {
        using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
        batch.SetAccount(SyncAccount(index), new Account(1, (ulong)index));
        batch.SetStorage(SyncAccount(index), (ulong)index, (ulong)index + 1);
        batch.SetStateTrieNode(SyncNodePath(index), Bytes.FromHexString("0xc0"));
    }

    private static Address SyncAccount(int index) => new(Keccak.Compute($"sync account {index}"));

    private static TreePath SyncNodePath(int index) => TreePath.FromHexString(index.ToString("x8"));

    private static void AssertSyncBatchesVisible(IPersistence.IPersistenceReader reader, IEnumerable<int> indexes)
    {
        using (Assert.EnterMultipleScope())
        {
            foreach (int index in indexes)
            {
                UInt256 slot = default;
                Assert.That(reader.GetAccount(SyncAccount(index))?.Balance, Is.EqualTo((UInt256)(ulong)index), $"account {index}");
                Assert.That(reader.TryGetSlot(SyncAccount(index), (ulong)index, ref slot) ? slot : default, Is.EqualTo((UInt256)((ulong)index + 1)), $"slot {index}");
                Assert.That(reader.TryLoadStateRlp(SyncNodePath(index), ReadFlags.None), Is.EqualTo(Bytes.FromHexString("0xc0")), $"node {index}");
            }
        }
    }

    private static Dictionary<string, byte[]> ReadFiles(string directory) =>
        Directory.GetFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(static path => path, File.ReadAllBytes);

    [Test]
    public async Task Crash_window_recovers_a_consistent_state_after_reopen([Values] bool paprikaCommitted)
    {
        WriteBlock(_persistence, 1);
        // The log's records of block 2 are durable either way; Paprika fails before its commit, or right after it.
        if (paprikaCommitted) _pages.FailAfterRootWrite = true;
        else _pages.PageWritesBeforeFailure = 0;

        Assert.That(() => WriteBlock(_persistence, 2), Throws.TypeOf<IOException>());
        Assert.That(() => WriteBlock(_persistence, 2), Throws.InvalidOperationException.With.Message.Contains("restart"), "the unconfirmed log takes no more batches");

        await Reopen();
        ulong recovered = paprikaCommitted ? 2UL : 1UL;
        Assert.That(ReadLatestBlock(_persistence), Is.EqualTo(Block(recovered)));

        WriteBlock(_persistence, recovered + 1);
        _log.Drain();
        Assert.That(ReadLatestBlock(_persistence), Is.EqualTo(Block(recovered + 1)));
    }

    [Test]
    public void A_batch_is_refused_when_the_state_moved_on_before_it_committed()
    {
        WriteBlock(_persistence, 1);
        // An import batch keeps the state pointer, which would take it back to block 1.
        IPersistence.IWriteBatch import = _persistence.CreateWriteBatch(State(1), State(1), WriteFlags.DisableWAL);
        import.SetAccount(Owner, new Account(99, 1));
        WriteBlock(_persistence, 2);

        Assert.That(import.Dispose, Throws.InvalidOperationException.With.Message.Contains("wrong state"));
        Assert.That(ReadLatestBlock(_persistence), Is.EqualTo(Block(2)));
    }

    public enum BatchKind
    {
        LogBacked,
        Import,
        Sync,
    }

    // A batch that writes the state pointer, and with it removes the wipe marker, commits a durable root.
    [TestCase(BatchKind.LogBacked, CommitOptions.FlushDataAndRoot)]
    [TestCase(BatchKind.Import, CommitOptions.FlushDataAndRoot)]
    [TestCase(BatchKind.Sync, CommitOptions.FlushDataOnly)]
    public void A_batch_commits_its_root_durably_unless_it_is_a_sync_batch(BatchKind kind, CommitOptions expected)
    {
        (StateId from, StateId to, WriteFlags flags) = kind switch
        {
            BatchKind.LogBacked => (StateId.PreGenesis, State(1), WriteFlags.None),
            BatchKind.Import => (StateId.PreGenesis, StateId.PreGenesis, WriteFlags.DisableWAL),
            _ => (StateId.Sync, StateId.Sync, WriteFlags.DisableWAL),
        };
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(from, to, flags)) batch.SetAccount(Owner, new Account(1, 1));

        Assert.That(_pages.RootWrites.Last(), Is.EqualTo(expected));
    }

    [Test]
    public void A_commit_returns_only_once_the_callers_own_buffer_is_committed()
    {
        WriteBlock(_persistence, 1);
        Task? queuedSync = null;
        (StateId, ulong?, ulong?) afterPersist = default;
        int persistCommits = 0;
        // The flush holds the commit lock, which is reentrant: a sync batch queues behind it, then the holder persists
        // a block, whose exclusive buffer is second in the queue.
        _pages.OnFlush = () =>
        {
            _pages.OnFlush = null;
            // On a thread of its own, so a busy thread pool cannot leave it unqueued.
            queuedSync = Task.Factory.StartNew(() => WriteSyncBatch(0), TaskCreationOptions.LongRunning);
            Thread.Sleep(200);
            int rootWrites = _pages.RootWrites.Count;
            WriteBlock(_persistence, 2);
            persistCommits = _pages.RootWrites.Count - rootWrites;
            afterPersist = ReadLatestBlock(_persistence);
        };
        _persistence.Flush();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistCommits, Is.EqualTo(2), "the queued sync batch's group, then the block alone");
            Assert.That(afterPersist, Is.EqualTo(Block(2)), "read right after the persist returned");
            Assert.That(queuedSync!.Wait(Timeout), Is.True);
        }

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        AssertSyncBatchesVisible(reader, [0]);
    }

    [Test]
    public void Concurrent_sync_batches_all_commit_and_are_visible()
    {
        const int writers = 8;
        const int batchesPerWriter = 25;
        long groupedCommits = Metrics.PaprikaFlatGroupedCommits;
        using ManualResetEventSlim gate = new(false);
        _pages.WriteGate = gate;
        Task[] tasks = Enumerable.Range(0, writers).Select(writer => Task.Run(() =>
        {
            for (int batch = 0; batch < batchesPerWriter; batch++) WriteSyncBatch(writer * batchesPerWriter + batch);
        })).ToArray();

        // The first commit is held while the other writers queue behind it, so they commit in groups.
        Assert.That(_pages.HeldWrites.Wait(Timeout), Is.True);
        Thread.Sleep(200);
        _pages.WriteGate = null;
        gate.Set();
        Assert.That(Task.WaitAll(tasks, Timeout), Is.True);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        AssertSyncBatchesVisible(reader, Enumerable.Range(0, writers * batchesPerWriter));
        Assert.That(Metrics.PaprikaFlatGroupedCommits, Is.GreaterThan(groupedCommits));
    }

    [Test]
    public async Task Readers_bind_flat_state_and_trie_nodes_to_one_commit_under_persists_and_merges([Values] bool secondLevel)
    {
        const ulong blocks = 300;
        _config.TrieNodeLogSecondLevelMergeLag = secondLevel ? 1 : -1;
        await Reopen();
        // Without the second level the first level merges into RocksDB, which the readers' snapshots then serve.
        IReadOnlyDictionary<TrieNodeLogLabel, long> merged = secondLevel ? Metrics.TrieNodeLogSecondLevelStoredBytes : Metrics.TrieNodeLogFlushedBytes;
        long mergedBytes = merged.GetValueOrDefault(TrieNodeLogLabel.State);
        ConcurrentQueue<string> mismatches = new();
        using CancellationTokenSource written = new();
        Task[] readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!written.IsCancellationRequested)
            {
                using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
                (StateId State, ulong? Nonce, ulong? TopNode) read = ReadBlock(reader);
                ulong number = read.State == StateId.PreGenesis ? 0 : read.State.BlockNumber;
                if (read != Block(number)) mismatches.Enqueue(read.ToString());
            }
        })).ToArray();

        for (ulong block = 1; block <= blocks; block++) WriteBlock(_persistence, block);
        written.Cancel();

        Assert.That(Task.WaitAll(readers, Timeout), Is.True);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(mismatches, Is.Empty);
            Assert.That(merged.GetValueOrDefault(TrieNodeLogLabel.State), Is.GreaterThan(mergedBytes), "generations were merged");
            Assert.That(ReadLatestBlock(_persistence), Is.EqualTo(Block(blocks)));
        }
    }

    [Test]
    public void A_long_reader_never_blocks_commits_and_its_lag_is_reported()
    {
        WriteBlock(_persistence, 1);
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();

        Assert.That(Task.Run(() =>
        {
            for (ulong block = 2; block <= 11; block++) WriteBlock(_persistence, block);
        }).Wait(Timeout), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadBlock(reader), Is.EqualTo(Block(1)));
            Assert.That(ReadLatestBlock(_persistence), Is.EqualTo(Block(11)));
            Assert.That(() => Metrics.PaprikaFlatOldestReaderLag, Is.EqualTo(10).After(5000, 50));
            Assert.That(() => Metrics.PaprikaFlatOpenReaders, Is.EqualTo(1).After(5000, 50));
        }
    }

    [Test]
    public async Task Layout_switch_is_refused_before_the_log_recovers([Values] bool toPaprikaFlat)
    {
        // The DB of the other layout, with blocks whose trie nodes are still in log generations.
        using SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new();
        string flatLogDirectory = Path.Combine(_directory.Path, "flatOnlyTrieNodeLog");
        IColumnsDb<FlatDbColumns> db = toPaprikaFlat ? flatDb : _columns;
        string logDirectory = toPaprikaFlat ? flatLogDirectory : LogDirectory;
        if (toPaprikaFlat)
        {
            await using TrieNodeLog flatLog = new(flatLogDirectory, FileSystem, flatDb, new ColumnsFlatMetadataStore(flatDb, _config), _config, LimboLogs.Instance);
            RocksDbPersistence flat = new(flatDb, LimboLogs.Instance, flatLog);
            for (ulong block = 1; block <= 3; block++) WriteBlock(flat, block);
        }
        else
        {
            for (ulong block = 1; block <= 3; block++) WriteBlock(_persistence, block);
            await Close();
        }

        Dictionary<string, byte[]> logFiles = ReadFiles(logDirectory);
        string paprikaDirectory = Path.Combine(_directory.Path, "flatOnlyPaprika");
        Assert.That(logFiles, Is.Not.Empty);
        if (toPaprikaFlat)
        {
            Assert.That(() => new PaprikaFlatStore(db, paprikaDirectory, () => PaprikaFlatStore.OpenFile(paprikaDirectory, FileSize, pageCacheBytes: 0, LimboLogs.Instance), ReaderWait, LimboLogs.Instance),
                Throws.InstanceOf<InvalidConfigurationException>());
        }
        else
        {
            Assert.That(() => new ColumnsFlatMetadataStore(db, _config), Throws.InstanceOf<InvalidConfigurationException>());
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadFiles(logDirectory), Is.EqualTo(logFiles));
            Assert.That(Directory.Exists(paprikaDirectory), Is.False);
        }

        // The original layout still reads every block back.
        if (toPaprikaFlat)
        {
            await using TrieNodeLog flatLog = new(flatLogDirectory, FileSystem, flatDb, new ColumnsFlatMetadataStore(flatDb, _config), _config, LimboLogs.Instance);
            Assert.That(ReadLatestBlock(new RocksDbPersistence(flatDb, LimboLogs.Instance, flatLog)), Is.EqualTo(Block(3)));
        }
        else
        {
            Open();
            Assert.That(ReadLatestBlock(_persistence), Is.EqualTo(Block(3)));
        }
    }

    public enum Refusal
    {
        LostPaprikaFile,
        BlankPaprikaFile,
        LostFlatDb,
        ShrunkPaprikaFile,
        NoTrieNodeLog,
    }

    [TestCase(Refusal.LostPaprikaFile, typeof(InvalidDataException))]
    [TestCase(Refusal.BlankPaprikaFile, typeof(InvalidDataException))]
    [TestCase(Refusal.LostFlatDb, typeof(InvalidDataException))]
    [TestCase(Refusal.ShrunkPaprikaFile, typeof(InvalidConfigurationException))]
    [TestCase(Refusal.NoTrieNodeLog, typeof(InvalidConfigurationException))]
    public async Task Startup_refuses_a_store_it_cannot_trust(Refusal refusal, Type expected)
    {
        WriteBlock(_persistence, 1);
        // Merges the trie nodes into RocksDB, which only a store that held state leaves behind.
        _persistence.Flush();
        await Close();
        if (refusal is Refusal.LostPaprikaFile or Refusal.BlankPaprikaFile) File.Delete(PaprikaFile);
        if (refusal == Refusal.BlankPaprikaFile) await File.WriteAllBytesAsync(PaprikaFile, []);

        using SnapshotableMemColumnsDb<FlatDbColumns> freshDb = new();
        using PaprikaFlatStore store = new(
            refusal == Refusal.LostFlatDb ? freshDb : _columns,
            PaprikaDirectory,
            refusal == Refusal.ShrunkPaprikaFile ? () => PaprikaFlatStore.OpenFile(PaprikaDirectory, FileSize / 2, pageCacheBytes: 0, LimboLogs.Instance) : OpenPaprika,
            ReaderWait,
            LimboLogs.Instance);

        // The trie node log reads the markers first, so a refusal there comes before it can recover against them.
        Func<object> start = refusal == Refusal.NoTrieNodeLog
            ? () => new PaprikaFlatPersistence(_columns, store, NullTrieNodeLog.Instance, _config)
            : () => store.Committed;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => start(), Throws.InstanceOf(expected));
            Assert.That(File.Exists(PaprikaFile), Is.EqualTo(refusal != Refusal.LostPaprikaFile), "a lost file is not recreated");
        }
    }

    [Test]
    public async Task A_first_open_that_failed_before_creating_the_file_starts_fresh_next_time()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        string paprikaDirectory = Path.Combine(_directory.Path, "freshPaprika");
        using (PaprikaFlatStore failing = new(db, paprikaDirectory, static () => throw new IOException("Injected open failure"), ReaderWait, LimboLogs.Instance))
        {
            await using TrieNodeLog log = new(Path.Combine(_directory.Path, "freshTrieNodeLog"), FileSystem, db, failing, _config, LimboLogs.Instance);
            Assert.That(() => new PaprikaFlatPersistence(db, failing, log, _config), Throws.TypeOf<IOException>());
        }

        Assert.That(File.Exists(MemoryMappedPageManager.GetPaprikaFilePath(paprikaDirectory)), Is.False);
        using PaprikaFlatStore store = new(db, paprikaDirectory, () => PaprikaFlatStore.OpenFile(paprikaDirectory, FileSize, pageCacheBytes: 0, LimboLogs.Instance), ReaderWait, LimboLogs.Instance);
        Assert.That(() => BasePersistence.ReadCurrentState(store.Committed), Is.EqualTo(StateId.PreGenesis));
    }

    [Test]
    public void A_first_open_interrupted_after_stamping_rocksdb_finishes_next_time()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        string paprikaDirectory = Path.Combine(_directory.Path, "freshPaprika");
        Directory.CreateDirectory(paprikaDirectory);
        File.WriteAllBytes(MemoryMappedPageManager.GetPaprikaFilePath(paprikaDirectory), []);
        BasePersistence.SetLayout(db.GetColumnDb(FlatDbColumns.Metadata), FlatLayout.PaprikaFlat);

        using PaprikaFlatStore store = new(db, paprikaDirectory, () => PaprikaFlatStore.OpenFile(paprikaDirectory, FileSize, pageCacheBytes: 0, LimboLogs.Instance), ReaderWait, LimboLogs.Instance);
        IReadOnlyKeyValueStore markers = store.Committed;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(BasePersistence.ReadLayout(markers), Is.EqualTo(FlatLayout.PaprikaFlat));
            Assert.That(BasePersistence.ReadCurrentState(markers), Is.EqualTo(StateId.PreGenesis));
        }
    }

    [Test]
    public async Task Markers_read_while_the_file_is_first_opened_wait_for_the_open()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        string paprikaDirectory = Path.Combine(_directory.Path, "freshPaprika");
        using ManualResetEventSlim opening = new(false);
        using ManualResetEventSlim proceed = new(false);
        using PaprikaFlatStore store = new(db, paprikaDirectory, () =>
        {
            opening.Set();
            proceed.Wait();
            return PaprikaFlatStore.OpenFile(paprikaDirectory, FileSize, pageCacheBytes: 0, LimboLogs.Instance);
        }, ReaderWait, LimboLogs.Instance);

        Task open = Task.Run(store.EnsureOpen);
        Assert.That(opening.Wait(Timeout), Is.True);
        Task<IReadOnlyKeyValueStore> markers = Task.Run(() => store.Committed);
        await Task.WhenAny(markers, Task.Delay(200));
        proceed.Set();
        await open;

        Assert.That(BasePersistence.ReadLayout(await markers), Is.EqualTo(FlatLayout.PaprikaFlat));
    }

    public enum ClearCrash
    {
        None,
        MarkingTheWipe,
        ClearingRocksDb,
        WipingPaprika,
    }

    [Test]
    public async Task Clear_recovers_from_a_crash_at_each_step([Values] ClearCrash crash)
    {
        WriteBlock(_persistence, 1);
        BasePersistence.WriteSnapCheckpoint(_columns, Bytes.FromHexString("0x01"));
        byte[] versionKey = _log.Shards[0].VersionKey;
        byte[]? version = _store.Committed.Get(versionKey);
        Assert.That(version, Is.Not.Null);

        switch (crash)
        {
            case ClearCrash.MarkingTheWipe:
                _pages.PageWritesBeforeFailure = 0;
                break;
            case ClearCrash.ClearingRocksDb:
                _columns.FailWriteBatches = true;
                break;
            case ClearCrash.WipingPaprika:
                _pages.PageWritesBeforeFailure = 1;
                break;
        }

        if (crash != ClearCrash.None)
        {
            Assert.That(() => _persistence.Clear(), Throws.TypeOf<IOException>());
            _columns.FailWriteBatches = false;
            await Reopen();

            using (Assert.EnterMultipleScope())
            {
                // Until the Paprika commit that marks it the wipe has not started; from there on it reads as interrupted.
                Assert.That(BasePersistence.ReadWipedForSync(_store.Committed), Is.EqualTo(crash != ClearCrash.MarkingTheWipe));
                Assert.That(BasePersistence.ReadCurrentState(_store.Committed), Is.EqualTo(State(1)));
                Assert.That(BasePersistence.ReadSnapCheckpoint(_rocksDb.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
                if (crash == ClearCrash.MarkingTheWipe) Assert.That(ReadLatestBlock(_persistence), Is.EqualTo(Block(1)));
            }
        }

        _persistence.Clear();

        using (Assert.EnterMultipleScope())
        {
            IReadOnlyKeyValueStore committed = _store.Committed;
            Assert.That(BasePersistence.ReadCurrentState(committed), Is.EqualTo(StateId.PreGenesis));
            Assert.That(BasePersistence.ReadWipedForSync(committed), Is.True);
            Assert.That(BasePersistence.ReadLayout(committed), Is.EqualTo(FlatLayout.PaprikaFlat));
            Assert.That(BasePersistence.ReadSlotEncoding(committed), Is.EqualTo(BasePersistence.SlotEncodingRlp));
            Assert.That(committed.Get(versionKey), Is.EqualTo(version), "log versions never regress");
            Assert.That(BasePersistence.ReadWipedForSync(_rocksDb.GetColumnDb(FlatDbColumns.Metadata)), Is.False, "RocksDB keeps no wipe marker of its own");
            Assert.That(BasePersistence.ReadSnapCheckpoint(_rocksDb.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
            Assert.That(ReadLatestBlock(_persistence), Is.EqualTo(Block(0)));
        }

        // The sync that follows the wipe persists from pre-genesis and closes it.
        WriteBlock(_persistence, 1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReadLatestBlock(_persistence), Is.EqualTo(Block(1)));
            Assert.That(BasePersistence.ReadWipedForSync(_store.Committed), Is.False);
        }
    }

    [Test]
    public void Flush_and_dispose_fsync_the_file_and_a_leaked_reader_keeps_it_mapped([Values] bool leakReader)
    {
        WriteBlock(_persistence, 1);
        int flushes = _pages.Flushes;
        _persistence.Flush();
        Assert.That(_pages.Flushes, Is.EqualTo(flushes + 1));

        using IColumnDbSnapshot<FlatDbColumns> rocksDbSnapshot = _rocksDb.CreateSnapshot();
        using IFlatMetadataStore.ISnapshot? leaked = leakReader ? _store.OpenSnapshot(rocksDbSnapshot) : null;
        _store.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_pages.Flushes, Is.EqualTo(flushes + 2));
            Assert.That(_pages.Disposed, Is.EqualTo(!leakReader));
            if (leaked is not null)
            {
                // Still mapped, so the leaked reader reads the file rather than faulting.
                PaprikaFlatReader paprikaReader = new(PaprikaFlatStore.SnapshotOf(leaked).Reader);
                Assert.That(paprikaReader.GetAccount(Owner.ToAccountPath, new byte[256]), Is.GreaterThan(0));
            }
        }
    }

    [Test]
    public async Task Disposing_refuses_commits_and_readers_while_it_waits_for_open_readers()
    {
        _readerWait = Timeout;
        await Reopen();
        WriteBlock(_persistence, 1);
        IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using ManualResetEventSlim flushing = new(false);
        _pages.OnFlush = () =>
        {
            _pages.OnFlush = null;
            flushing.Set();
        };
        Task disposing = Task.Run(_store.Dispose);
        Assert.That(flushing.Wait(Timeout), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => WriteBlock(_persistence, 2), Throws.InstanceOf<ObjectDisposedException>());
            Assert.That(() => _persistence.CreateReader().Dispose(), Throws.InstanceOf<ObjectDisposedException>());
        }

        reader.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(disposing.Wait(Timeout), Is.True);
            Assert.That(_pages.Disposed, Is.True);
        }
    }

    [Test]
    public void A_reader_whose_markers_are_corrupt_is_closed()
    {
        WriteBlock(_persistence, 1);
        PagedDb db = _store.EnsureOpen();
        using (Paprika.IBatch corrupt = db.BeginNextBatch())
        {
            corrupt.SetRaw(PaprikaFlatMetadata.BlobKey, Bytes.FromHexString("0x02"));
            corrupt.Commit(CommitOptions.FlushDataAndRoot).AsTask().GetAwaiter().GetResult();
        }

        Assert.That(() => _persistence.CreateReader(), Throws.TypeOf<InvalidDataException>());
        Assert.That(db.GetReaderStats().OpenReaders, Is.Zero, "no reader pins the file's pages");
    }

    [Test]
    public async Task A_full_file_fails_the_batch_without_poisoning_the_store()
    {
        const long smallFile = 1024 * 1024;
        using SnapshotableMemColumnsDb<FlatDbColumns> db = new();
        using PaprikaFlatStore store = new(db, null, () => PagedDb.NativeMemoryDb(smallFile, PaprikaFlatStore.HistoryDepth), ReaderWait, LimboLogs.Instance);
        await using TrieNodeLog log = new(Path.Combine(_directory.Path, "smallTrieNodeLog"), FileSystem, db, store, _config, LimboLogs.Instance);
        PaprikaFlatPersistence persistence = new(db, store, log, _config);

        Assert.That(() =>
        {
            using IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.Sync, StateId.Sync, WriteFlags.DisableWAL);
            for (int account = 0; account < 50_000; account++) batch.SetAccount(SyncAccount(account), new Account(1, 1));
        }, Throws.InvalidOperationException.With.Message.Contains(nameof(IFlatDbConfig.PaprikaFlatFileSizeBytes)));

        WriteBlock(persistence, 1);
        Assert.That(ReadLatestBlock(persistence), Is.EqualTo(Block(1)));
    }

    /// <summary>A page manager that counts its flushes, and fails or holds Paprika's commits on demand.</summary>
    private sealed class FaultyPageManager(IPageManager inner) : IPageManager
    {
        private int _flushes;

        /// <summary>Page writes that succeed before every later one fails; negative never fails.</summary>
        public int PageWritesBeforeFailure { get; set; } = -1;

        /// <summary>Whether a root write throws once it is durable, as a crash right after a commit.</summary>
        public bool FailAfterRootWrite { get; set; }

        /// <summary>A gate page writes wait for while it is set; a waiting write first releases <see cref="HeldWrites"/>.</summary>
        public ManualResetEventSlim? WriteGate { get; set; }

        public SemaphoreSlim HeldWrites { get; } = new(0);

        /// <summary>The option of every root write, in order.</summary>
        public ConcurrentQueue<CommitOptions> RootWrites { get; } = new();

        /// <summary>Runs on every flush, before it.</summary>
        public Action? OnFlush { get; set; }

        public int Flushes => Volatile.Read(ref _flushes);

        public bool Disposed { get; private set; }

        public int MaxPage => inner.MaxPage;

        public Page GetAt(DbAddress address) => inner.GetAt(address);

        public void Prefetch(DbAddress address) => inner.Prefetch(address);

        public DbAddress GetAddress(in Page page) => inner.GetAddress(page);

        public Page GetAtForWriting(DbAddress address, bool reused) => inner.GetAtForWriting(address, reused);

        public ValueTask WritePages(ICollection<DbAddress> addresses, CommitOptions options)
        {
            if (WriteGate is { } gate)
            {
                HeldWrites.Release();
                gate.Wait();
            }

            if (PageWritesBeforeFailure == 0) throw new IOException("Injected page write failure");
            if (PageWritesBeforeFailure > 0) PageWritesBeforeFailure--;
            return inner.WritePages(addresses, options);
        }

        public async ValueTask WriteRootPage(DbAddress rootPage, CommitOptions options)
        {
            await inner.WriteRootPage(rootPage, options);
            RootWrites.Enqueue(options);
            if (FailAfterRootWrite) throw new IOException("Injected failure after the root write");
        }

        public void Flush()
        {
            Interlocked.Increment(ref _flushes);
            OnFlush?.Invoke();
            inner.Flush();
        }

        public void ForceFlush() => inner.ForceFlush();

        public void Dispose()
        {
            Disposed = true;
            inner.Dispose();
        }
    }

    /// <summary>The flat DB, with its write batches failing on demand.</summary>
    private sealed class FaultyColumnsDb(IColumnsDb<FlatDbColumns> inner) : IColumnsDb<FlatDbColumns>
    {
        public bool FailWriteBatches { get; set; }

        public IEnumerable<FlatDbColumns> ColumnKeys => inner.ColumnKeys;

        public Db.IDb GetColumnDb(FlatDbColumns key) => inner.GetColumnDb(key);

        public IColumnsWriteBatch<FlatDbColumns> StartWriteBatch() =>
            FailWriteBatches ? throw new IOException("Injected write batch failure") : inner.StartWriteBatch();

        public IColumnDbSnapshot<FlatDbColumns> CreateSnapshot() => inner.CreateSnapshot();

        public IColumnDbSnapshot<FlatDbColumns> CreateSnapshot(bool sequentialReadAhead) => inner.CreateSnapshot(sequentialReadAhead);

        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);

        // The test owns the inner DB.
        public void Dispose() { }
    }
}
