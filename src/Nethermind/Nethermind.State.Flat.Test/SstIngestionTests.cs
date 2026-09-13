// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.RocksDbBindings;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class SstIngestionTests
{
    private static readonly Address Addr = TestItem.AddressA;
    private static readonly UInt256 Slot1 = 1, Slot2 = 2, Slot3 = 3;

    private string _dbPath = null!;
    private ColumnsDb<FlatDbColumns> _db = null!;
    private ObservablePersistence _persistence = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "flat-sst-ingest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dbPath);
        _db = new ColumnsDb<FlatDbColumns>(
            _dbPath,
            new DbSettings("State", _dbPath) { DeleteOnStart = true },
            new DbConfig(),
            new RocksDbConfigFactory(new DbConfig(), new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
            LimboLogs.Instance,
            Enum.GetValues<FlatDbColumns>());
        _persistence = new ObservablePersistence(_db, LimboLogs.Instance, new FlatDbConfig { PersistViaSstIngestion = true });
    }

    [TearDown]
    public void TearDown()
    {
        _persistence.Dispose();
        _db.Dispose();
        try { Directory.Delete(_dbPath, true); }
        catch (Exception e) { TestContext.Out.WriteLine($"Failed to delete {_dbPath}: {e}"); }
    }

    [Test]
    public void Unrepairable_torn_base_is_fatal_and_keeps_the_marker_for_roll_forward()
    {
        StateId s1 = State(1, 1);
        StateId s2 = State(2, 2);

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(100));
        }

        ColumnDb storageColumn = (ColumnDb)_db.GetColumnDb(FlatDbColumns.Storage);
        storageColumn._testIngestFailureHook = () => throw new IOException("injected SST ingest failure");

        Assert.That(() =>
        {
            using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(s1, s2, WriteFlags.None);
            batch.SetAccount(Addr, new Account(200));
            batch.SetStorage(Addr, Slot2, Slot(0x22));
        }, Throws.InstanceOf<IOException>());

        storageColumn._testIngestFailureHook = null;

        using (Assert.EnterMultipleScope())
        {
            // The account column went live at s2 while the pointer stayed at s1: releasing the gate would publish
            // that torn base to every later reader snapshot, so the commit must stop the node instead.
            Assert.That(_persistence.FatalShutdownCount, Is.EqualTo(1));
            Assert.That(BasePersistence.ReadIngestMarker(_db.GetColumnDb(FlatDbColumns.Metadata))?.To, Is.EqualTo(s2));
            Assert.That(StagedSstFiles(), Is.Not.Empty);
        }
    }

    [Test]
    public void Pending_marker_is_not_overwritten_by_the_next_persist()
    {
        StateId s1 = State(1, 1);
        StateId s2 = State(2, 2);
        StateId s3 = State(3, 3);

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(100));
        }

        ColumnDb storageColumn = (ColumnDb)_db.GetColumnDb(FlatDbColumns.Storage);
        storageColumn._testIngestFailureHook = () => throw new IOException("injected SST ingest failure");

        Assert.That(() =>
        {
            using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(s1, s2, WriteFlags.None);
            batch.SetAccount(Addr, new Account(200));
            batch.SetStorage(Addr, Slot2, Slot(0x22));
        }, Throws.InstanceOf<IOException>());

        storageColumn._testIngestFailureHook = null;

        (StateId To, string[] Files)? pending = BasePersistence.ReadIngestMarker(_db.GetColumnDb(FlatDbColumns.Metadata));
        Assert.That(pending, Is.Not.Null);
        string[] pendingFiles = StagedSstFiles();

        // The marker is a single slot. Taking it here would orphan the files above and strand the account column
        // ahead of the pointer with nothing left to roll it forward.
        Assert.That(() =>
        {
            using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(s1, s3, WriteFlags.None);
            batch.SetAccount(Addr, new Account(300));
        }, Throws.InstanceOf<InvalidOperationException>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BasePersistence.ReadIngestMarker(_db.GetColumnDb(FlatDbColumns.Metadata))?.To, Is.EqualTo(pending!.Value.To));
            Assert.That(StagedSstFiles(), Is.SupersetOf(pendingFiles));
        }

        Reopen();

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            // Roll forward completes the interrupted commit, not the one that was refused.
            Assert.That(reader.CurrentState, Is.EqualTo(s2));
            Assert.That(reader.GetAccount(Addr)!.Balance, Is.EqualTo((UInt256)200));
            Assert.That(BasePersistence.ReadIngestMarker(_db.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
        }
    }

    [Test]
    public void Empty_pointer_bump_does_not_clobber_a_pending_marker()
    {
        StateId s1 = State(1, 1);
        StateId s2 = State(2, 2);
        StateId s3 = State(3, 3);

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(100));
        }

        ColumnDb storageColumn = (ColumnDb)_db.GetColumnDb(FlatDbColumns.Storage);
        storageColumn._testIngestFailureHook = () => throw new IOException("injected SST ingest failure");

        Assert.That(() =>
        {
            using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(s1, s2, WriteFlags.None);
            batch.SetAccount(Addr, new Account(200));
            batch.SetStorage(Addr, Slot2, Slot(0x22));
        }, Throws.InstanceOf<IOException>());

        storageColumn._testIngestFailureHook = null;

        // The shape Importer and FlatTreeSyncStore use: a write-free batch purely to advance the pointer. Without
        // the pending-marker check it would clear the marker and advance having written nothing, stranding the
        // already-live account column at s2 forever.
        Assert.That(() =>
        {
            using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(s1, s3, WriteFlags.None);
        }, Throws.InstanceOf<InvalidOperationException>());

        Assert.That(BasePersistence.ReadIngestMarker(_db.GetColumnDb(FlatDbColumns.Metadata))?.To, Is.EqualTo(s2));

        Reopen();

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(s2));
    }

    [Test]
    public void Marker_committed_by_a_failing_batch_is_cleared_with_its_staged_files()
    {
        StateId s1 = State(1, 1);
        StateId s2 = State(2, 2);

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(100));
        }

        // The marker batch commits on Dispose and only then reports a failure, so the marker is durable even
        // though the persist threw: the rollback must clear it, not delete the files it still references.
        WrapWithWriteBatchFaults().FailWriteBatchAfter(0, commitBeforeFailing: true);

        Assert.That(() =>
        {
            using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(s1, s2, WriteFlags.None);
            batch.SetAccount(Addr, new Account(200));
        }, Throws.InstanceOf<IOException>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BasePersistence.ReadIngestMarker(_db.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
            Assert.That(StagedSstFiles(), Is.Empty);
        }

        Reopen();

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            // A marker outliving its deleted files would advance the pointer here over data never ingested.
            Assert.That(reader.CurrentState, Is.EqualTo(s1));
            Assert.That(reader.GetAccount(Addr)!.Balance, Is.EqualTo((UInt256)100));
        }
    }

    [Test]
    public void Failed_pointer_advance_after_every_ingest_is_completed_inline()
    {
        StateId s1 = State(1, 1);
        StateId s2 = State(2, 2);

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(100));
        }

        // Every column goes live at s2 and only the pointer write fails. Releasing the gate there would publish a
        // base ahead of its pointer, so the commit must be rolled forward inline instead.
        WrapWithWriteBatchFaults().FailWriteBatchAfter(1);

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(s1, s2, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(200));
            batch.SetStorage(Addr, Slot1, Slot(0x11));
        }

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_persistence.FatalShutdownCount, Is.Zero);
            Assert.That(reader.CurrentState, Is.EqualTo(s2));
            Assert.That(reader.GetAccount(Addr)!.Balance, Is.EqualTo((UInt256)200));
            AssertSlot(reader, Slot1, Slot(0x11));
            Assert.That(BasePersistence.ReadIngestMarker(_db.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
            Assert.That(StagedSstFiles(), Is.Empty);
        }
    }

    [Test]
    public void Unclearable_marker_after_a_failed_persist_is_fatal_and_rolls_forward()
    {
        StateId s1 = State(1, 1);
        StateId s2 = State(2, 2);

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(100));
        }

        ColumnDb accountColumn = (ColumnDb)_db.GetColumnDb(FlatDbColumns.Account);
        accountColumn._testIngestFailureHook = () => throw new IOException("injected SST ingest failure");

        // Nothing is ingested, so the rollback owns the marker - but its clearing batch fails too. The marker then
        // outlives the persist and every later one refuses to start, so the node must stop instead of stalling.
        WrapWithWriteBatchFaults().FailWriteBatchAfter(1);

        Assert.That(() =>
        {
            using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(s1, s2, WriteFlags.None);
            batch.SetAccount(Addr, new Account(200));
        }, Throws.InstanceOf<IOException>());

        accountColumn._testIngestFailureHook = null;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_persistence.FatalShutdownCount, Is.EqualTo(1));
            Assert.That(BasePersistence.ReadIngestMarker(_db.GetColumnDb(FlatDbColumns.Metadata))?.To, Is.EqualTo(s2));
            Assert.That(StagedSstFiles(), Is.Not.Empty);
        }

        Reopen();

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(s2));
            Assert.That(reader.GetAccount(Addr)!.Balance, Is.EqualTo((UInt256)200));
        }
    }

    /// <summary>Observes the fatal-exit decision instead of taking it, which a test process cannot survive.</summary>
    private sealed class ObservablePersistence(IColumnsDb<FlatDbColumns> db, ILogManager logManager, IFlatDbConfig config)
        : RocksDbPersistence(db, logManager, config)
    {
        public int FatalShutdownCount { get; private set; }

        protected override void FatalShutdown() => FatalShutdownCount++;
    }

    /// <summary>Re-points <see cref="_persistence"/> at the same DB through a write-batch fault injector.</summary>
    private WriteBatchFaultingColumnsDb WrapWithWriteBatchFaults()
    {
        WriteBatchFaultingColumnsDb faulting = new(_db);
        _persistence = new ObservablePersistence(faulting, LimboLogs.Instance, new FlatDbConfig { PersistViaSstIngestion = true });
        return faulting;
    }

    /// <summary>Fails one chosen write batch of the real DB, optionally after it has already committed.</summary>
    /// <remarks>Forwards only what the SST-ingest persist path uses.</remarks>
    private sealed class WriteBatchFaultingColumnsDb(IColumnsDb<FlatDbColumns> inner) : IColumnsDb<FlatDbColumns>
    {
        private int _skipBatches = -1;
        private bool _commitBeforeFailing;

        /// <summary>Fails the write batch started after <paramref name="skip"/> further ones, once.</summary>
        /// <param name="commitBeforeFailing">Whether the failing batch commits before throwing, the shape a
        /// throw from <c>Dispose</c> leaves behind.</param>
        public void FailWriteBatchAfter(int skip, bool commitBeforeFailing = false)
        {
            _skipBatches = skip;
            _commitBeforeFailing = commitBeforeFailing;
        }

        public IColumnsWriteBatch<FlatDbColumns> StartWriteBatch()
        {
            IColumnsWriteBatch<FlatDbColumns> batch = inner.StartWriteBatch();
            if (_skipBatches < 0) return batch;
            if (_skipBatches > 0)
            {
                _skipBatches--;
                return batch;
            }

            _skipBatches = -1;
            return new FaultingWriteBatch(batch, _commitBeforeFailing);
        }

        public IDb GetColumnDb(FlatDbColumns key) => inner.GetColumnDb(key);
        public IEnumerable<FlatDbColumns> ColumnKeys => inner.ColumnKeys;
        public IColumnDbSnapshot<FlatDbColumns> CreateSnapshot() => inner.CreateSnapshot();
        public void Flush(bool onlyWal = false) => inner.Flush(onlyWal);
        public void SyncWal() => inner.SyncWal();
        public void Dispose() => inner.Dispose();

        private sealed class FaultingWriteBatch(IColumnsWriteBatch<FlatDbColumns> inner, bool commitBeforeFailing)
            : IColumnsWriteBatch<FlatDbColumns>
        {
            public IWriteBatch GetColumnBatch(FlatDbColumns key) => inner.GetColumnBatch(key);
            public void Clear() => inner.Clear();

            public void Dispose()
            {
                // Disposing the batch is what commits it, so dropping the writes means clearing them first.
                if (!commitBeforeFailing) inner.Clear();
                inner.Dispose();
                throw new IOException("injected write batch failure");
            }
        }
    }

    private static SlotValue Slot(byte v) => SlotValue.FromSpanWithoutLeadingZero(new byte[] { v });
    private static StateId State(ulong number, byte seed) => new(number, ValueKeccak.Compute(new byte[] { seed }));

    private void Reopen(bool persistViaSstIngestion = true)
    {
        _persistence.Dispose();
        _db.Dispose();
        _db = new ColumnsDb<FlatDbColumns>(
            _dbPath,
            new DbSettings("State", _dbPath),
            new DbConfig(),
            new RocksDbConfigFactory(new DbConfig(), new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
            LimboLogs.Instance,
            Enum.GetValues<FlatDbColumns>());
        _persistence = new ObservablePersistence(_db, LimboLogs.Instance, new FlatDbConfig { PersistViaSstIngestion = persistViaSstIngestion });
    }

    private string[] StagedSstFiles()
    {
        string stagingDir = Path.Combine(_dbPath, "sst_ingest");
        return Directory.Exists(stagingDir) ? Directory.GetFiles(stagingDir, "*.sst") : [];
    }

    private static void AssertSlot(IPersistence.IPersistenceReader reader, in UInt256 slot, SlotValue expected)
    {
        SlotValue read = default;
        Assert.That(reader.TryGetSlot(Addr, slot, ref read), Is.True);
        Assert.That(read.AsReadOnlySpan.ToArray(), Is.EqualTo(expected.AsReadOnlySpan.ToArray()));
    }

    private static void AssertSlotAbsent(IPersistence.IPersistenceReader reader, in UInt256 slot)
    {
        SlotValue read = default;
        Assert.That(reader.TryGetSlot(Addr, slot, ref read), Is.False);
    }

    [Test]
    public void Ingest_duplicate_and_delete_across_chunk_boundary_last_write_wins()
    {
        IDb accountDb = _db.GetColumnDb(FlatDbColumns.Account);
        byte[] overwrittenKey = ValueKeccak.Compute("overwritten"u8).ToByteArray();
        byte[] deletedKey = ValueKeccak.Compute("deleted"u8).ToByteArray();
        byte[] dedupedKey = ValueKeccak.Compute("deduped"u8).ToByteArray();

        using (ISstIngestWriteBatch batch = ((ISstIngestible)accountDb).StartSstIngestBatch())
        {
            batch.Set(overwrittenKey, [0x01]);
            batch.Set(deletedKey, [0x0d]);
            batch.Set(dedupedKey, [0x0a]);
            batch.Set(dedupedKey, [0x0b]);

            byte[] filler = new byte[64 * 1024];
            Span<byte> key = stackalloc byte[32];
            for (int i = 0; i < 2100; i++)
            {
                BitConverter.TryWriteBytes(key, i);
                key[8] = 0xff;
                batch.Set(key, filler);
            }

            batch.Set(overwrittenKey, [0x02]);
            batch.Set(deletedKey, null);

            batch.SealToStagedFiles();
            batch.IngestStagedFiles();
        }

        Assert.That(accountDb.Get(overwrittenKey), Is.EqualTo(new byte[] { 0x02 }));
        Assert.That(accountDb.Get(deletedKey), Is.Null);
        Assert.That(accountDb.Get(dedupedKey), Is.EqualTo(new byte[] { 0x0b }));
    }

    [Test]
    public void Ingest_zero_length_write_at_exact_slab_boundary_round_trips()
    {
        const int slabSize = 1 << 20;
        const int entrySize = 1024;
        const int keySize = 32;
        IDb accountDb = _db.GetColumnDb(FlatDbColumns.Account);
        byte[] probeKey = ValueKeccak.Compute("probe"u8).ToByteArray();
        byte[] probeValue = new byte[entrySize - keySize];
        probeValue[0] = 0x42;

        using (ISstIngestWriteBatch batch = ((ISstIngestible)accountDb).StartSstIngestBatch())
        {
            byte[] filler = new byte[entrySize - keySize];
            Span<byte> key = stackalloc byte[keySize];
            for (int i = 0; i < slabSize / entrySize - 1; i++)
            {
                BitConverter.TryWriteBytes(key, i);
                key[8] = 0xee;
                batch.Set(key, filler);
            }

            batch.Set(probeKey, probeValue);
            batch.PutSpan(default, default);

            batch.SealToStagedFiles();
            batch.IngestStagedFiles();
        }

        Assert.That(accountDb.Get(probeKey), Is.EqualTo(probeValue));
    }

    [Test]
    public void Ingest_round_trips_and_advances_pointer()
    {
        StateId s1 = State(1, 1);
        SlotValue v1 = Slot(0x11), v2 = Slot(0x22);

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(100));
            batch.SetStorage(Addr, Slot1, v1);
            batch.SetStorage(Addr, Slot2, v2);
        }

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        AssertSlot(reader, Slot1, v1);
        AssertSlot(reader, Slot2, v2);
        Assert.That(reader.GetAccount(Addr), Is.Not.Null);
        Assert.That(reader.CurrentState, Is.EqualTo(s1));
    }

    [Test]
    public void Ingest_self_destruct_then_recreate_keeps_last_write_and_tombstones_the_rest()
    {
        StateId s1 = State(1, 1);
        StateId s2 = State(2, 2);
        SlotValue v1 = Slot(0x11), v2 = Slot(0x22), v1b = Slot(0x1b), v3 = Slot(0x33);

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(100));
            batch.SetStorage(Addr, Slot1, v1);
            batch.SetStorage(Addr, Slot2, v2);
        }

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(s1, s2, WriteFlags.None))
        {
            batch.SelfDestruct(Addr);
            batch.SetStorage(Addr, Slot1, v1b);
            batch.SetStorage(Addr, Slot3, v3);
        }

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        AssertSlot(reader, Slot1, v1b);
        AssertSlot(reader, Slot3, v3);
        AssertSlotAbsent(reader, Slot2);
        Assert.That(reader.CurrentState, Is.EqualTo(s2));
    }

    [Test]
    public void Failed_ingest_leaves_pointer_and_staging_dir_untouched()
    {
        StateId s1 = State(1, 1);
        StateId s2 = State(2, 2);
        SlotValue v1 = Slot(0x11), v2 = Slot(0x22);

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(100));
            batch.SetStorage(Addr, Slot1, v1);
        }

        ColumnDb accountColumn = (ColumnDb)_db.GetColumnDb(FlatDbColumns.Account);
        accountColumn._testIngestFailureHook = () => throw new IOException("injected SST ingest failure");

        Assert.That(() =>
        {
            using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(s1, s2, WriteFlags.None);
            batch.SetAccount(Addr, new Account(200));
            batch.SetStorage(Addr, Slot2, v2);
        }, Throws.InstanceOf<IOException>());

        accountColumn._testIngestFailureHook = null;

        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(s1));
        }

        Assert.That(StagedSstFiles(), Is.Empty);
        Assert.That(BasePersistence.ReadIngestMarker(_db.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
    }

    [Test]
    public void Failed_ingest_after_first_column_keeps_marker_and_rolls_forward_on_reopen()
    {
        StateId s1 = State(1, 1);
        StateId s2 = State(2, 2);
        TreePath topPath = new(Keccak.Compute("top"), 4);
        TreePath deepPath = new(Keccak.Compute("deep"), 10);
        TreePath fallbackPath = new(Keccak.Compute("fallback"), 20);
        Hash256 storageAccount = TestItem.KeccakA;
        TreePath storagePath = new(Keccak.Compute("storage"), 8);
        byte[] payload = [0x02, 0x02];
        SlotValue v2 = Slot(0x22);

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(100));
        }

        ColumnDb storageColumn = (ColumnDb)_db.GetColumnDb(FlatDbColumns.Storage);
        storageColumn._testIngestFailureHook = () => throw new IOException("injected SST ingest failure");

        Assert.That(() =>
        {
            using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(s1, s2, WriteFlags.None);
            batch.SetAccount(Addr, new Account(200));
            batch.SetStorage(Addr, Slot2, v2);
            batch.SetStateTrieNode(topPath, payload);
            batch.SetStateTrieNode(deepPath, payload);
            batch.SetStateTrieNode(fallbackPath, payload);
            batch.SetStorageTrieNode(storageAccount, storagePath, payload);
        }, Throws.InstanceOf<IOException>());

        storageColumn._testIngestFailureHook = null;

        (StateId To, string[] Files)? marker = BasePersistence.ReadIngestMarker(_db.GetColumnDb(FlatDbColumns.Metadata));
        Assert.That(marker, Is.Not.Null);
        Assert.That(marker!.Value.To, Is.EqualTo(s2));
        Assert.That(StagedSstFiles(), Is.Not.Empty);
        Assert.That(_persistence.FatalShutdownCount, Is.EqualTo(1),
            "an unrepairable torn base must stop the node rather than serve it to readers");

        Reopen();

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(s2));
        Assert.That(reader.GetAccount(Addr)!.Balance, Is.EqualTo((UInt256)200));
        AssertSlot(reader, Slot2, v2);
        Assert.That(reader.TryLoadStateRlp(topPath, ReadFlags.None), Is.EqualTo(payload));
        Assert.That(reader.TryLoadStateRlp(deepPath, ReadFlags.None), Is.EqualTo(payload));
        Assert.That(reader.TryLoadStateRlp(fallbackPath, ReadFlags.None), Is.EqualTo(payload));
        Assert.That(reader.TryLoadStorageRlp(storageAccount, storagePath, ReadFlags.None), Is.EqualTo(payload));
        Assert.That(BasePersistence.ReadIngestMarker(_db.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
        Assert.That(StagedSstFiles(), Is.Empty);
    }

    [Test]
    public void Failed_ingest_after_first_column_completes_inline_when_the_retry_succeeds()
    {
        // Torn-live-state fix: when a later column ingest fails after an earlier one already went live, the commit
        // is completed inline (still under the write lock) so no reader ever observes a torn base. A transient
        // failure (one-shot here) is rolled forward inline - the pointer advances to `to` with no reopen and no
        // leftover marker, and the batch dispose does not surface an error.
        StateId s1 = State(1, 1);
        StateId s2 = State(2, 2);
        TreePath topPath = new(Keccak.Compute("top"), 4);
        TreePath deepPath = new(Keccak.Compute("deep"), 10);
        TreePath fallbackPath = new(Keccak.Compute("fallback"), 20);
        Hash256 storageAccount = TestItem.KeccakA;
        TreePath storagePath = new(Keccak.Compute("storage"), 8);
        byte[] payload = [0x02, 0x02];
        SlotValue v2 = Slot(0x22);

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(100));
        }

        ColumnDb storageColumn = (ColumnDb)_db.GetColumnDb(FlatDbColumns.Storage);
        // Fail the storage ingest once, then clear the hook so the inline roll-forward's retry succeeds.
        storageColumn._testIngestFailureHook = () =>
        {
            storageColumn._testIngestFailureHook = null;
            throw new IOException("injected transient SST ingest failure");
        };

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(s1, s2, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(200));
            batch.SetStorage(Addr, Slot2, v2);
            batch.SetStateTrieNode(topPath, payload);
            batch.SetStateTrieNode(deepPath, payload);
            batch.SetStateTrieNode(fallbackPath, payload);
            batch.SetStorageTrieNode(storageAccount, storagePath, payload);
        }

        Assert.That(storageColumn._testIngestFailureHook, Is.Null, "the injected failure should have fired exactly once");
        Assert.That(BasePersistence.ReadIngestMarker(_db.GetColumnDb(FlatDbColumns.Metadata)), Is.Null, "the marker is cleared by the inline completion");
        Assert.That(StagedSstFiles(), Is.Empty);

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(s2));
        Assert.That(reader.GetAccount(Addr)!.Balance, Is.EqualTo((UInt256)200));
        AssertSlot(reader, Slot2, v2);
        Assert.That(reader.TryLoadStateRlp(topPath, ReadFlags.None), Is.EqualTo(payload));
        Assert.That(reader.TryLoadStateRlp(deepPath, ReadFlags.None), Is.EqualTo(payload));
        Assert.That(reader.TryLoadStateRlp(fallbackPath, ReadFlags.None), Is.EqualTo(payload));
        Assert.That(reader.TryLoadStorageRlp(storageAccount, storagePath, ReadFlags.None), Is.EqualTo(payload));
    }

    [Test]
    public void Crash_between_column_ingests_rolls_forward_on_reopen()
    {
        StateId s1 = State(1, 1);
        StateId s2 = State(2, 2);
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(100));
        }

        byte[] accountKey = ValueKeccak.Compute("crash-account"u8).ToByteArray();
        byte[] storageKey = ValueKeccak.Compute("crash-storage"u8).ToByteArray();

        ISstIngestWriteBatch accountBatch = ((ISstIngestible)_db.GetColumnDb(FlatDbColumns.Account)).StartSstIngestBatch();
        ISstIngestWriteBatch storageBatch = ((ISstIngestible)_db.GetColumnDb(FlatDbColumns.Storage)).StartSstIngestBatch();
        try
        {
            accountBatch.Set(accountKey, [0xa2]);
            storageBatch.Set(storageKey, [0xb2]);
            List<string> stagedFiles = [.. accountBatch.SealToStagedFiles(), .. storageBatch.SealToStagedFiles()];

            using (IColumnsWriteBatch<FlatDbColumns> markerBatch = _db.StartWriteBatch())
                BasePersistence.SetIngestMarker(markerBatch.GetColumnBatch(FlatDbColumns.Metadata), s2, stagedFiles);
            _db.Flush(onlyWal: true);

            // The "crash": Account already ingested, Storage still staged, pointer never advanced.
            accountBatch.IngestStagedFiles();
        }
        finally
        {
            accountBatch.Dispose();
            storageBatch.Dispose();
        }

        Reopen();

        Assert.That(_db.GetColumnDb(FlatDbColumns.Account).Get(accountKey), Is.EqualTo(new byte[] { 0xa2 }));
        Assert.That(_db.GetColumnDb(FlatDbColumns.Storage).Get(storageKey), Is.EqualTo(new byte[] { 0xb2 }));
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(s2));
        }
        Assert.That(BasePersistence.ReadIngestMarker(_db.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
        Assert.That(StagedSstFiles(), Is.Empty);
    }

    [Test]
    public void Crash_after_all_ingests_completes_pointer_on_reopen()
    {
        StateId s1 = State(1, 1);
        StateId s2 = State(2, 2);
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(100));
        }

        byte[] accountKey = ValueKeccak.Compute("crash-account"u8).ToByteArray();

        ISstIngestWriteBatch accountBatch = ((ISstIngestible)_db.GetColumnDb(FlatDbColumns.Account)).StartSstIngestBatch();
        try
        {
            accountBatch.Set(accountKey, [0xa3]);
            List<string> stagedFiles = [.. accountBatch.SealToStagedFiles()];

            using (IColumnsWriteBatch<FlatDbColumns> markerBatch = _db.StartWriteBatch())
                BasePersistence.SetIngestMarker(markerBatch.GetColumnBatch(FlatDbColumns.Metadata), s2, stagedFiles);
            _db.Flush(onlyWal: true);

            // The "crash": everything ingested, only the pointer advance is missing.
            accountBatch.IngestStagedFiles();
        }
        finally
        {
            accountBatch.Dispose();
        }

        Reopen();

        Assert.That(_db.GetColumnDb(FlatDbColumns.Account).Get(accountKey), Is.EqualTo(new byte[] { 0xa3 }));
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(s2));
        }
        Assert.That(BasePersistence.ReadIngestMarker(_db.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
        Assert.That(StagedSstFiles(), Is.Empty);
    }

    [Test]
    public void Startup_sweep_deletes_orphaned_staged_files_without_marker()
    {
        StateId s1 = State(1, 1);
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(100));
        }

        string stagingDir = Path.Combine(_dbPath, "sst_ingest");
        Directory.CreateDirectory(stagingDir);
        File.WriteAllBytes(Path.Combine(stagingDir, "Account_9999.sst"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(stagingDir, "StorageNodes_10000.sst"), [4, 5, 6]);

        Reopen();

        Assert.That(StagedSstFiles(), Is.Empty);
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(s1));
        Assert.That(reader.GetAccount(Addr), Is.Not.Null);
    }

    [Test]
    public void Concurrent_readers_never_observe_torn_cross_column_state()
    {
        const int PersistCount = 25;
        TreePath topPath = new(Keccak.Compute("top"), 4);
        TreePath deepPath = new(Keccak.Compute("deep"), 10);
        TreePath fallbackPath = new(Keccak.Compute("fallback"), 20);
        Hash256 storageAccount = TestItem.KeccakA;
        TreePath storagePath = new(Keccak.Compute("storage"), 8);

        StateId[] states = new StateId[PersistCount + 1];
        for (int i = 1; i <= PersistCount; i++) states[i] = State((ulong)i, (byte)i);

        void Persist(int i)
        {
            using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(
                i == 1 ? StateId.PreGenesis : states[i - 1], states[i], WriteFlags.None);
            byte[] payload = [0x01, (byte)i];
            batch.SetAccount(Addr, new Account((ulong)i, (UInt256)i));
            batch.SetStorage(Addr, Slot1, Slot((byte)i));
            batch.SetStateTrieNode(topPath, payload);
            batch.SetStateTrieNode(deepPath, payload);
            batch.SetStateTrieNode(fallbackPath, payload);
            batch.SetStorageTrieNode(storageAccount, storagePath, payload);
        }

        Persist(1);

        using CancellationTokenSource done = new();
        Task[] readers = [.. Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
                ulong n = reader.CurrentState.BlockNumber;
                Assert.That(n, Is.InRange(1UL, (ulong)PersistCount));
                byte[] expected = [0x01, (byte)n];

                Account? account = reader.GetAccount(Addr);
                Assert.That(account, Is.Not.Null);
                Assert.That(account!.Nonce, Is.EqualTo(n), "Account column is torn relative to the pointer");

                SlotValue slotValue = default;
                Assert.That(reader.TryGetSlot(Addr, Slot1, ref slotValue), Is.True);
                Assert.That(slotValue.AsReadOnlySpan.ToArray(), Is.EqualTo(Slot((byte)n).AsReadOnlySpan.ToArray()), "Storage column is torn relative to the pointer");

                Assert.That(reader.TryLoadStateRlp(topPath, ReadFlags.None), Is.EqualTo(expected), "StateTopNodes column is torn relative to the pointer");
                Assert.That(reader.TryLoadStateRlp(deepPath, ReadFlags.None), Is.EqualTo(expected), "StateNodes column is torn relative to the pointer");
                Assert.That(reader.TryLoadStateRlp(fallbackPath, ReadFlags.None), Is.EqualTo(expected), "FallbackNodes column is torn relative to the pointer");
                Assert.That(reader.TryLoadStorageRlp(storageAccount, storagePath, ReadFlags.None), Is.EqualTo(expected), "StorageNodes column is torn relative to the pointer");
            }
        }))];

        for (int i = 2; i <= PersistCount; i++) Persist(i);
        done.Cancel();
        Task.WaitAll(readers);
    }

    [Test]
    public void PersistViaSstIngestion_on_MemDb_falls_back_and_round_trips()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> memDb = new();
        RocksDbPersistence persistence = new(memDb, LimboLogs.Instance, new FlatDbConfig { PersistViaSstIngestion = true });
        StateId s1 = State(1, 1);
        SlotValue v1 = Slot(0x11);

        using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
        {
            batch.SetStorage(Addr, Slot1, v1);
        }

        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        AssertSlot(reader, Slot1, v1);
        Assert.That(reader.CurrentState, Is.EqualTo(s1));
    }

    [Test]
    public void Same_state_and_disable_wal_batches_bypass_the_ingest_path()
    {
        StateId s1 = State(1, 1);
        StateId s2 = State(2, 2);
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(100));
        }

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(s1, s1, WriteFlags.None))
        {
            batch.SetStorage(Addr, Slot1, Slot(0x11));
        }
        Assert.That(StagedSstFiles(), Is.Empty);
        Assert.That(BasePersistence.ReadIngestMarker(_db.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);

        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(s1, s2, WriteFlags.DisableWAL))
        {
            batch.SetStorage(Addr, Slot2, Slot(0x22));
        }
        Assert.That(StagedSstFiles(), Is.Empty);
        Assert.That(BasePersistence.ReadIngestMarker(_db.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
    }

    [Test]
    public void Interrupted_ingest_rolls_forward_on_reopen_even_with_sst_ingestion_disabled()
    {
        StateId s1 = State(1, 1);
        StateId s2 = State(2, 2);
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(100));
        }

        byte[] accountKey = ValueKeccak.Compute("flagoff-account"u8).ToByteArray();
        byte[] storageKey = ValueKeccak.Compute("flagoff-storage"u8).ToByteArray();

        ISstIngestWriteBatch accountBatch = ((ISstIngestible)_db.GetColumnDb(FlatDbColumns.Account)).StartSstIngestBatch();
        ISstIngestWriteBatch storageBatch = ((ISstIngestible)_db.GetColumnDb(FlatDbColumns.Storage)).StartSstIngestBatch();
        try
        {
            accountBatch.Set(accountKey, [0xa2]);
            storageBatch.Set(storageKey, [0xb2]);
            List<string> stagedFiles = [.. accountBatch.SealToStagedFiles(), .. storageBatch.SealToStagedFiles()];

            using (IColumnsWriteBatch<FlatDbColumns> markerBatch = _db.StartWriteBatch())
                BasePersistence.SetIngestMarker(markerBatch.GetColumnBatch(FlatDbColumns.Metadata), s2, stagedFiles);
            _db.Flush(onlyWal: true);

            accountBatch.IngestStagedFiles();
        }
        finally
        {
            accountBatch.Dispose();
            storageBatch.Dispose();
        }

        Reopen(persistViaSstIngestion: false);

        Assert.That(_db.GetColumnDb(FlatDbColumns.Account).Get(accountKey), Is.EqualTo(new byte[] { 0xa2 }));
        Assert.That(_db.GetColumnDb(FlatDbColumns.Storage).Get(storageKey), Is.EqualTo(new byte[] { 0xb2 }));
        using (IPersistence.IPersistenceReader reader = _persistence.CreateReader())
        {
            Assert.That(reader.CurrentState, Is.EqualTo(s2));
        }
        Assert.That(BasePersistence.ReadIngestMarker(_db.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
        Assert.That(StagedSstFiles(), Is.Empty);
    }

    [Test]
    public void Ingest_corruption_fast_shuts_down_and_schedules_a_repair_only_for_the_live_db(
        [Values] bool corruptionNamesStagedFile)
    {
        _db.Dispose();
        ObservableColumnsDb observable = new(
            _dbPath,
            new DbSettings("State", _dbPath) { DeleteOnStart = true },
            new DbConfig(),
            new RocksDbConfigFactory(new DbConfig(), new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
            LimboLogs.Instance,
            Enum.GetValues<FlatDbColumns>());
        _db = observable;
        _persistence = new ObservablePersistence(_db, LimboLogs.Instance, new FlatDbConfig { PersistViaSstIngestion = true });

        StateId s1 = State(1, 1);
        StateId s2 = State(2, 2);
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
        {
            batch.SetAccount(Addr, new Account(100));
        }

        ColumnDb accountColumn = (ColumnDb)_db.GetColumnDb(FlatDbColumns.Account);
        accountColumn._testIngestFailureHook = () => throw new RocksDbException(
            corruptionNamesStagedFile
                ? $"Corruption: injected external SST corruption in {StagedFileName(FlatDbColumns.Account)}"
                : "Corruption: injected corruption of the live DB");

        Assert.That(() =>
        {
            using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(s1, s2, WriteFlags.None);
            batch.SetAccount(Addr, new Account(200));
            batch.SetStorage(Addr, Slot1, Slot(0x11));
        }, Throws.InstanceOf<RocksDbException>());

        accountColumn._testIngestFailureHook = null;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(observable.FatalShutdownCount, Is.EqualTo(1));
            // Ingestion also flushes the memtable and reads live SST metadata, so corruption raised there is the
            // live DB's unless the message names a staged file - and only the live DB's is worth repairing.
            Assert.That(
                Directory.GetFiles(_dbPath, "corrupt.marker", SearchOption.AllDirectories),
                corruptionNamesStagedFile ? Is.Empty : Is.Not.Empty);
        }
    }

    private string StagedFileName(FlatDbColumns column)
    {
        foreach (string path in StagedSstFiles())
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith($"{column}_", StringComparison.Ordinal)) return name;
        }

        throw new InvalidOperationException($"No staged SST file for column {column}");
    }

    private sealed class ObservableColumnsDb(
        string basePath,
        DbSettings settings,
        IDbConfig dbConfig,
        IRocksDbConfigFactory rocksDbConfigFactory,
        ILogManager logManager,
        IReadOnlyList<FlatDbColumns> keys)
        : ColumnsDb<FlatDbColumns>(basePath, settings, dbConfig, rocksDbConfigFactory, logManager, keys)
    {
        public int FatalShutdownCount { get; private set; }

        protected override void FatalShutdown() => FatalShutdownCount++;
    }
}
