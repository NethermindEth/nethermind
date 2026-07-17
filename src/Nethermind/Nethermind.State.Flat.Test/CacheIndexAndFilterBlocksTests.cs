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

[TestFixture]
public class CacheIndexAndFilterBlocksTests
{
    private static readonly Address Addr = TestItem.AddressA;
    private static readonly UInt256 Slot1 = 1;

    private static SlotValue Slot(byte v) => SlotValue.FromSpanWithoutLeadingZero(new byte[] { v });
    private static StateId State(ulong number, byte seed) => new(number, ValueKeccak.Compute(new byte[] { seed }));

    private static void AssertSlot(IPersistence.IPersistenceReader reader, in UInt256 slot, SlotValue expected)
    {
        SlotValue read = default;
        Assert.That(reader.TryGetSlot(Addr, slot, ref read), Is.True);
        Assert.That(read.AsReadOnlySpan.ToArray(), Is.EqualTo(expected.AsReadOnlySpan.ToArray()));
    }

    [Test]
    public void CacheIndexAndFilterBlocks_partitioned_filters_open_and_round_trip()
    {
        string path = Path.Combine(Path.GetTempPath(), "flat-cif-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        DbConfig dbConfig = new() { CacheIndexAndFilterBlocks = true };
        StateId s1 = State(1, 1);
        SlotValue v1 = Slot(0x11);
        try
        {
            // Write, then force a full flush so the data lands in an SST built with the partitioned
            // index/filter format - the batch's own dispose only flushes the WAL, leaving it in the memtable.
            using (ColumnsDb<FlatDbColumns> db = new(
                path,
                new DbSettings("State", path) { DeleteOnStart = true },
                dbConfig,
                new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
                LimboLogs.Instance,
                Enum.GetValues<FlatDbColumns>()))
            {
                RocksDbPersistence persistence = new(db, LimboLogs.Instance, new FlatDbConfig());
                using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
                {
                    batch.SetAccount(Addr, new Account(100));
                    batch.SetStorage(Addr, Slot1, v1);
                }

                db.Flush();
            }

            // Reopen without deleting so the reads round-trip from the on-disk SST, exercising the
            // partitioned index/filter format on both table open and lookup.
            using (ColumnsDb<FlatDbColumns> db = new(
                path,
                new DbSettings("State", path) { DeleteOnStart = false },
                dbConfig,
                new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
                LimboLogs.Instance,
                Enum.GetValues<FlatDbColumns>()))
            {
                RocksDbPersistence persistence = new(db, LimboLogs.Instance, new FlatDbConfig());
                using IPersistence.IPersistenceReader reader = persistence.CreateReader();
                AssertSlot(reader, Slot1, v1);
                Assert.That(reader.GetAccount(Addr), Is.Not.Null);
            }
        }
        finally
        {
            try { Directory.Delete(path, true); } catch { }
        }
    }
}
