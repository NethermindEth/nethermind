// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

/// <summary>
/// Covers the one-shot conversion of a <see cref="FlatLayout.PreimageFlatV1"/> storage column into the
/// <see cref="FlatLayout.PreimageFlat"/> key shape.
/// </summary>
[TestFixture]
public class PreimageStorageKeyMigrationTests
{
    // Two addresses sharing a mined 4-byte lead, which is exactly what the V1 shape handles badly.
    private static readonly Address AddressA = new("0x00000000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly Address AddressB = new("0x00000000bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
    private static readonly Address AddressC = TestItem.AddressC;

    private static readonly UInt256[] Slots = [UInt256.Zero, 1, 42, 12345, UInt256.MaxValue];

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private MemDb _scratchDb = null!;

    [SetUp]
    public void Setup()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _scratchDb = new MemDb();
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _scratchDb.Dispose();
    }

    [Test]
    public void Run_ConvertsStorageKeys_AndStampsLayout()
    {
        WriteState(FlatLayout.PreimageFlatV1);
        int keyCountBefore = StorageKeyCount();

        Assert.That(CreateMigration().Run(CancellationToken.None), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(BasePersistence.ReadLayout(_db.GetColumnDb(FlatDbColumns.Metadata)), Is.EqualTo(FlatLayout.PreimageFlat));
            Assert.That(StorageKeyCount(), Is.EqualTo(keyCountBefore));
        }

        AssertStateReadsBack();
    }

    /// <summary>The point of the new shape: an account's slots form a range of their own, even under a shared lead.</summary>
    [Test]
    public void Run_MakesAccountSlotsContiguous()
    {
        WriteState(FlatLayout.PreimageFlatV1);
        CreateMigration().Run(CancellationToken.None);

        // A reader built on the migrated layout also asserts the stamp landed, since its constructor validates it.
        using IPersistence.IPersistenceReader reader = CreatePersistence(FlatLayout.PreimageFlat).CreateReader();
        using IPersistence.IFlatIterator iterator = reader.CreateStorageIterator(AsPreimageKey(AddressA));

        List<UInt256> scanned = [];
        while (iterator.MoveNext()) scanned.Add(new UInt256(iterator.CurrentKey.Bytes, isBigEndian: true));

        // AddressB shares AddressA's 4-byte lead, so under V1 this scan would also walk B's slots.
        Assert.That(scanned, Is.EquivalentTo(Slots));
    }

    [Test]
    public void Run_IsNoop_WhenAlreadyMigrated()
    {
        WriteState(FlatLayout.PreimageFlatV1);
        CreateMigration().Run(CancellationToken.None);

        Assert.That(CreateMigration().Run(CancellationToken.None), Is.False);
        AssertStateReadsBack();
    }

    [TestCase(FlatLayout.Flat)]
    [TestCase(FlatLayout.FlatInTrie)]
    public void Run_Throws_ForNonPreimageLayout(FlatLayout layout)
    {
        BasePersistence.SetLayout(_db.GetColumnDb(FlatDbColumns.Metadata), layout);

        Assert.Throws<InvalidConfigurationException>(() => CreateMigration().Run(CancellationToken.None));
    }

    [Test]
    public void Run_DoesNotRedumpAConvertedColumn_WhenResuming()
    {
        WriteState(FlatLayout.PreimageFlatV1);

        // Leave the DB exactly as a crash between the dump and the restore would: a complete scratch dump and an
        // untouched storage column. Re-dumping here would convert the already-converted keys a second time.
        PreimageStorageKeyMigration migration = CreateMigration();
        migration.DumpToScratch(_db.GetColumnDb(FlatDbColumns.Storage), _scratchDb, CancellationToken.None);
        byte[] sentinelKey = Bytes.FromHexString("0xfeedfacefeedface");
        _scratchDb.Set(sentinelKey, [0xff]);
        _db.GetColumnDb(FlatDbColumns.Metadata).Set(PreimageStorageKeyMigration.DumpCompleteKey, [1]);

        Assert.That(migration.Run(CancellationToken.None), Is.True);

        // The sentinel is only in the storage column if the restore used the pre-existing dump.
        Assert.That(_db.GetColumnDb(FlatDbColumns.Storage).Get(sentinelKey), Is.EqualTo(new byte[] { 0xff }));
        AssertStateReadsBack();
    }

    private PreimageStorageKeyMigration CreateMigration() => new(_db, () => _scratchDb, LimboLogs.Instance);

    private IPersistence CreatePersistence(FlatLayout layout) => new PreimageRocksdbPersistence(_db, LimboLogs.Instance, layout);

    private int StorageKeyCount() => _db.GetColumnDb(FlatDbColumns.Storage).GetAllKeys().Count();

    private void WriteState(FlatLayout layout)
    {
        using IPersistence.IWriteBatch writeBatch = CreatePersistence(layout)
            .CreateWriteBatch(StateId.PreGenesis, StateId.PreGenesis, WriteFlags.None);

        foreach (Address address in (Address[])[AddressA, AddressB, AddressC])
        {
            writeBatch.SetAccount(address, TestItem.GenerateIndexedAccount(0));
            foreach (UInt256 slot in Slots)
            {
                writeBatch.SetStorage(address, slot, SlotValue.FromSpanWithoutLeadingZero(SlotValueOf(address, slot)));
            }
        }
    }

    private void AssertStateReadsBack()
    {
        using IPersistence.IPersistenceReader reader = CreatePersistence(FlatLayout.PreimageFlat).CreateReader();
        using (Assert.EnterMultipleScope())
        {
            foreach (Address address in (Address[])[AddressA, AddressB, AddressC])
            {
                Assert.That(reader.GetAccount(address), Is.EqualTo(TestItem.GenerateIndexedAccount(0)), address.ToString());
                foreach (UInt256 slot in Slots)
                {
                    SlotValue value = default;
                    reader.TryGetSlot(address, slot, ref value);
                    Assert.That(value.ToEvmBytes(), Is.EqualTo(SlotValueOf(address, slot)), $"{address} slot {slot}");
                }
            }
        }
    }

    /// <summary>A value unique to the pair, so a key mixed up between accounts or slots cannot go unnoticed.</summary>
    private static byte[] SlotValueOf(Address address, in UInt256 slot)
    {
        Span<byte> preimage = stackalloc byte[Address.Size + 32];
        address.Bytes.CopyTo(preimage);
        slot.ToBigEndian(preimage[Address.Size..]);
        return Keccak.Compute(preimage).Bytes.WithoutLeadingZeros().ToArray();
    }

    /// <summary>Preimage mode fakes the address hash by copying the address into the leading bytes.</summary>
    private static ValueHash256 AsPreimageKey(Address address)
    {
        ValueHash256 key = ValueKeccak.Zero;
        address.Bytes.CopyTo(key.BytesAsSpan);
        return key;
    }
}
