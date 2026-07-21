// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// Pins the flat storage column's key layout: a slot's key is its EIP-8297 tree key, so the column
/// enumerates in stem order. The import rebuild depends on that ordering, and nothing else in the
/// codebase would notice if it broke.
/// </summary>
public class PbtStorageKeyLayoutTests
{
    private static EvmWord Word(byte value) => EvmWordSlot.FromStripped([value]);

    /// <summary>
    /// Round-trips slots on both sides of the header boundary: slots below
    /// <see cref="PbtKeyDerivation.HeaderStorageOffset"/> live on the account header stem in zone 0,
    /// the rest on their own storage-zone stem, and a zero value deletes the entry either way.
    /// </summary>
    [TestCase(0u, TestName = "header slot, first")]
    [TestCase(63u, TestName = "header slot, last")]
    [TestCase(64u, TestName = "storage-zone slot, first")]
    [TestCase(1000u, TestName = "storage-zone slot")]
    public void SlotRoundTripsAndZeroDeletes(uint slot)
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db);

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, TestItem.KeccakA.ValueHash256), WriteFlags.None))
        {
            batch.SetSlot(TestItem.AddressA, slot, Word(0xAB));
        }

        using (IPbtPersistence.IReader reader = persistence.CreateReader())
        {
            Assert.That(reader.GetSlot(TestItem.AddressA, slot), Is.EqualTo(Word(0xAB)));

            // a different address must not collide onto the same key
            Assert.That(EvmWordSlot.IsZero(reader.GetSlot(TestItem.AddressB, slot)), Is.True);
        }

        ValueHash256 treeKey = PbtKeyDerivation.StorageKey(TestItem.AddressA, slot);
        Assert.That(db.GetColumnDb(PbtColumns.Storage)[treeKey.Bytes.ToArray()], Is.Not.Null);

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(new StateId(1, TestItem.KeccakA.ValueHash256), new StateId(2, TestItem.KeccakB.ValueHash256), WriteFlags.None))
        {
            batch.SetSlot(TestItem.AddressA, slot, default);
        }

        using (IPbtPersistence.IReader reader = persistence.CreateReader())
        {
            Assert.That(EvmWordSlot.IsZero(reader.GetSlot(TestItem.AddressA, slot)), Is.True);
        }
    }

    /// <summary>
    /// The property the import rebuild rests on: whatever order slots are written in, an ordered scan
    /// of the storage column returns them in ascending tree-key order, with the zone-0 header slots
    /// ahead of the zone-8 storage slots.
    /// </summary>
    [Test]
    public void StorageColumnEnumeratesInTreeKeyOrder()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db);

        Address[] addresses = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC];
        UInt256[] slots = [5, 70, 1000, 63, 64, 0, 100_000];

        List<ValueHash256> written = [];
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, TestItem.KeccakA.ValueHash256), WriteFlags.None))
        {
            foreach (Address address in addresses)
            {
                foreach (UInt256 slot in slots)
                {
                    batch.SetSlot(address, slot, Word(0x11));
                    written.Add(PbtKeyDerivation.StorageKey(address, slot));
                }
            }
        }

        // one byte longer than any key, so it sorts above every one of them
        byte[] pastEverything = new byte[33];
        pastEverything.AsSpan().Fill(0xFF);

        List<ValueHash256> scanned = [];
        ISortedKeyValueStore storage = (ISortedKeyValueStore)db.GetColumnDb(PbtColumns.Storage);
        using (ISortedView view = storage.GetViewBetween(new byte[32], pastEverything))
        {
            while (view.MoveNext()) scanned.Add(new ValueHash256(view.CurrentKey));
        }

        written.Sort(static (a, b) => a.Bytes.SequenceCompareTo(b.Bytes));
        Assert.That(scanned, Is.EqualTo(written));

        // zone 0 (header slots, on the account stem) sorts ahead of zone 8 (storage stems)
        int firstStorageZone = scanned.FindIndex(static key => (key.Bytes[0] & 0x80) != 0);
        Assert.That(firstStorageZone, Is.GreaterThan(0));
        Assert.That(scanned.GetRange(0, firstStorageZone), Has.All.Matches<ValueHash256>(static key => key.Bytes[0] >> 4 == PbtKeyDerivation.AccountZone));
    }

    /// <summary>A database written under an older key layout must be refused, not silently read as all-zero slots.</summary>
    [Test]
    public void RejectsADatabaseWrittenUnderAnEarlierLayout()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db);

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, TestItem.KeccakA.ValueHash256), WriteFlags.None))
        {
            batch.SetSlot(TestItem.AddressA, 1, Word(0xAB));
        }

        // strip the version stamp, leaving a populated database that looks like a pre-versioning one
        db.GetColumnDb(PbtColumns.Metadata).Remove("layoutVersion"u8);

        Assert.That(() => new PbtRocksDbPersistence(db), Throws.InstanceOf<InvalidDataException>());
    }

    /// <summary>An empty database has no state to misread, so it is stamped with the current layout rather than refused.</summary>
    [Test]
    public void StampsAFreshDatabase()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        _ = new PbtRocksDbPersistence(db);

        Assert.That(db.GetColumnDb(PbtColumns.Metadata)["layoutVersion"u8.ToArray()], Is.Not.Null);
        Assert.That(() => new PbtRocksDbPersistence(db), Throws.Nothing);
    }
}
