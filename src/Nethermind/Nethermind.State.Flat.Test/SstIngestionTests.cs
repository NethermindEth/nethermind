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
    private RocksDbPersistence _persistence = null!;

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
        _persistence = new RocksDbPersistence(_db, LimboLogs.Instance, new FlatDbConfig { PersistViaSstIngestion = true });
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        try { Directory.Delete(_dbPath, true); } catch { }
    }

    private static SlotValue Slot(byte v) => SlotValue.FromSpanWithoutLeadingZero(new byte[] { v });
    private static StateId State(ulong number, byte seed) => new(number, ValueKeccak.Compute(new byte[] { seed }));

    private void Reopen()
    {
        _db.Dispose();
        _db = new ColumnsDb<FlatDbColumns>(
            _dbPath,
            new DbSettings("State", _dbPath),
            new DbConfig(),
            new RocksDbConfigFactory(new DbConfig(), new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
            LimboLogs.Instance,
            Enum.GetValues<FlatDbColumns>());
        _persistence = new RocksDbPersistence(_db, LimboLogs.Instance, new FlatDbConfig { PersistViaSstIngestion = true });
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
}
