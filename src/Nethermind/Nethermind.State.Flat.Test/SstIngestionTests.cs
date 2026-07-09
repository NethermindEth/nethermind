// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
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
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

// Exercises the --FlatDb.PersistViaSstIngestion path end-to-end on a real RocksDB. The MemDb-backed tests only
// cover the default WriteBatch path (MemDb is not ISstIngestible and falls back), so without this the ingest code
// is untested. Covers: ingest round-trip, the SelfDestruct+recreate last-write-wins dedup (which an SST forbids as
// a duplicate key), delete tombstones, the currentState pointer advance, and the MemDb fallback.
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
        try { Directory.Delete(_dbPath, true); } catch { /* best effort */ }
    }

    private static SlotValue Slot(byte v) => SlotValue.FromSpanWithoutLeadingZero(new byte[] { v });
    private static StateId State(ulong number, byte seed) => new(number, ValueKeccak.Compute(new byte[] { seed }));

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

        // SelfDestruct clears all existing storage (delete tombstones for slot1 + slot2), then slot1 is recreated
        // and slot3 added. The ingested SST must collapse slot1's delete+put to the put (last write wins, no
        // duplicate key) and keep slot2 tombstoned.
        using (IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(s1, s2, WriteFlags.None))
        {
            batch.SelfDestruct(Addr);
            batch.SetStorage(Addr, Slot1, v1b);
            batch.SetStorage(Addr, Slot3, v3);
        }

        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        AssertSlot(reader, Slot1, v1b);  // recreated — last write wins
        AssertSlot(reader, Slot3, v3);   // added
        AssertSlotAbsent(reader, Slot2); // tombstoned by self-destruct
        Assert.That(reader.CurrentState, Is.EqualTo(s2));
    }

    [Test]
    public void PersistViaSstIngestion_on_MemDb_falls_back_and_round_trips()
    {
        // MemDb is not ISstIngestible, so the flag falls back to the WriteBatch path — must still round-trip.
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
