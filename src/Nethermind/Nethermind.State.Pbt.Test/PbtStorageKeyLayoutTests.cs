// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Pbt.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Pbt.Test;

/// <summary>
/// Pins where a slot lands on disk: which leaf column its stem belongs to, and that an ordered scan of
/// a leaf column returns its stems ascending. The import rebuild rests on that ordering, and nothing
/// else in the codebase would notice if it broke.
/// </summary>
public class PbtStorageKeyLayoutTests
{
    private static EvmWord Word(byte value) => EvmWordSlot.FromStripped([value]);

    /// <summary>
    /// Round-trips slots on both sides of the header boundary: slots below
    /// <see cref="PbtKeyDerivation.HeaderStorageOffset"/> live on the account header stem in zone 0,
    /// the rest on their own storage-zone stem, and an emptied stem reads back as zero either way.
    /// </summary>
    [TestCase(0u, PbtColumns.AccountLeaves, TestName = "header slot, first")]
    [TestCase(63u, PbtColumns.AccountLeaves, TestName = "header slot, last")]
    [TestCase(64u, PbtColumns.StorageLeaves, TestName = "storage-zone slot, first")]
    [TestCase(1000u, PbtColumns.StorageLeaves, TestName = "storage-zone slot")]
    public void SlotRoundTripsThroughItsStemsBlob(uint slot, PbtColumns expectedColumn)
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());
        Stem stem = StemOf(TestItem.AddressA, slot, out byte subIndex);

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, TestItem.KeccakA.ValueHash256), default, WriteFlags.None))
        {
            batch.SetLeafBlob(stem, Blob(subIndex, 0xAB));
        }

        using (IPbtPersistence.IReader reader = persistence.CreateReader())
        {
            Assert.That(reader.GetSlot(TestItem.AddressA, slot), Is.EqualTo(Word(0xAB)));

            // a different address must not collide onto the same stem
            Assert.That(EvmWordSlot.IsZero(reader.GetSlot(TestItem.AddressB, slot)), Is.True);
        }

        Assert.That(db.GetColumnDb(expectedColumn)[stem.Bytes.ToArray()], Is.Not.Null);

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(new StateId(1, TestItem.KeccakA.ValueHash256), new StateId(2, TestItem.KeccakB.ValueHash256), default, WriteFlags.None))
        {
            batch.SetLeafBlob(stem, null);
        }

        using (IPbtPersistence.IReader reader = persistence.CreateReader())
        {
            Assert.That(EvmWordSlot.IsZero(reader.GetSlot(TestItem.AddressA, slot)), Is.True);
        }
    }

    /// <summary>
    /// The property the import rebuild rests on: whatever order stems are written in, an ordered scan
    /// of a leaf column returns them ascending, and the zone-0 header stems live in a column of their
    /// own ahead of the zone-8 storage stems.
    /// </summary>
    [Test]
    public void LeafColumnsEnumerateInStemOrder()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());

        Address[] addresses = [TestItem.AddressA, TestItem.AddressB, TestItem.AddressC];
        UInt256[] slots = [5, 70, 1000, 63, 64, 0, 100_000];

        // sets, not lists: an account's header slots share one stem, and so do the 256 slots of a tree index
        HashSet<Stem> headerStems = [];
        HashSet<Stem> storageStems = [];
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, TestItem.KeccakA.ValueHash256), default, WriteFlags.None))
        {
            foreach (Address address in addresses)
            {
                foreach (UInt256 slot in slots)
                {
                    Stem stem = StemOf(address, slot, out byte subIndex);
                    batch.SetLeafBlob(stem, Blob(subIndex, 0x11));
                    (PbtKeyDerivation.IsHeaderSlot(slot) ? headerStems : storageStems).Add(stem);
                }
            }
        }

        List<Stem> headerScan = ScanStems(db, PbtColumns.AccountLeaves);
        Assert.That(headerScan, Is.EqualTo(Ascending(headerStems)).AsCollection);
        Assert.That(headerScan, Has.All.Matches<Stem>(static stem => stem.Zone == PbtKeyDerivation.AccountZone));

        List<Stem> storageScan = ScanStems(db, PbtColumns.StorageLeaves);
        Assert.That(storageScan, Is.EqualTo(Ascending(storageStems)).AsCollection);
        // the storage zone is the first nibble's high bit rather than one nibble value
        Assert.That(storageScan, Has.All.Matches<Stem>(static stem => stem.Zone >= 0x8));
    }

    /// <summary>A database written under an older key layout must be refused, not silently read as all-zero slots.</summary>
    [Test]
    public void RejectsADatabaseWrittenUnderAnEarlierLayout()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, new PbtConfig());

        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(1, TestItem.KeccakA.ValueHash256), default, WriteFlags.None))
        {
            batch.SetLeafBlob(StemOf(TestItem.AddressA, 1, out byte subIndex), Blob(subIndex, 0xAB));
        }

        // strip the version stamp, leaving a populated database that looks like a pre-versioning one
        db.GetColumnDb(PbtColumns.Metadata).Remove("layoutVersion"u8);

        Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig()), Throws.InstanceOf<InvalidDataException>());
    }

    /// <summary>An empty database has no state to misread, so it is stamped with the current layout rather than refused.</summary>
    [Test]
    public void StampsAFreshDatabase()
    {
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        _ = new PbtRocksDbPersistence(db, new PbtConfig());

        Assert.That(db.GetColumnDb(PbtColumns.Metadata)["layoutVersion"u8.ToArray()], Is.Not.Null);
        Assert.That(() => new PbtRocksDbPersistence(db, new PbtConfig()), Throws.Nothing);
    }

    private static List<Stem> ScanStems(SnapshotableMemColumnsDb<PbtColumns> db, PbtColumns column)
    {
        // one byte longer than any stem, so it sorts above every one of them
        byte[] pastEverything = new byte[Stem.Length + 1];
        pastEverything.AsSpan().Fill(0xFF);

        List<Stem> scanned = [];
        using ISortedView view = ((ISortedKeyValueStore)db.GetColumnDb(column)).GetViewBetween(new byte[Stem.Length], pastEverything);
        while (view.MoveNext()) scanned.Add(new Stem(view.CurrentKey));
        return scanned;
    }

    private static List<Stem> Ascending(HashSet<Stem> stems)
    {
        List<Stem> ordered = [.. stems];
        ordered.Sort(static (a, b) => a.Bytes.SequenceCompareTo(b.Bytes));
        return ordered;
    }

    private static Stem StemOf(Address address, in UInt256 slot, out byte subIndex)
    {
        PbtSlotKeyDeriver deriver = new(address);
        return deriver.Derive(slot, out subIndex);
    }

    private static byte[] Blob(byte subIndex, byte value)
    {
        StemLeafBlobBuilder builder = new();
        builder.Set(subIndex, [value]);
        return builder.Encode();
    }
}
