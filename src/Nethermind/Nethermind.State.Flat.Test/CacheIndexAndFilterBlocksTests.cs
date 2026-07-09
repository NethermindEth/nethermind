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
        // Opening the DB with CacheIndexAndFilterBlocks=true appends the partitioned index/filter options
        // (cache_index_and_filter_blocks + two-level index + partition_filters + pin_top_level) to the RocksDB
        // options string. If RocksDB rejected that combination, the column-family open below would throw. A
        // successful write→read round-trip proves the options parse and are accepted by the engine.
        string path = Path.Combine(Path.GetTempPath(), "flat-cif-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        DbConfig dbConfig = new() { CacheIndexAndFilterBlocks = true };
        try
        {
            using ColumnsDb<FlatDbColumns> db = new(
                path,
                new DbSettings("State", path) { DeleteOnStart = true },
                dbConfig,
                new RocksDbConfigFactory(dbConfig, new PruningConfig(), new TestHardwareInfo(), LimboLogs.Instance, validateConfig: false),
                LimboLogs.Instance,
                Enum.GetValues<FlatDbColumns>());
            RocksDbPersistence persistence = new(db, LimboLogs.Instance, new FlatDbConfig());

            StateId s1 = State(1, 1);
            SlotValue v1 = Slot(0x11);
            using (IPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, s1, WriteFlags.None))
            {
                batch.SetAccount(Addr, new Account(100));
                batch.SetStorage(Addr, Slot1, v1);
            }

            using IPersistence.IPersistenceReader reader = persistence.CreateReader();
            AssertSlot(reader, Slot1, v1);
            Assert.That(reader.GetAccount(Addr), Is.Not.Null);
        }
        finally
        {
            try { Directory.Delete(path, true); } catch { /* best effort */ }
        }
    }
}
