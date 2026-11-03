// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence.PaprikaFlat;
using NUnit.Framework;
using Paprika.Store;
using static Nethermind.State.Flat.Test.Persistence.PaprikaFlat.PaprikaFlatTestDb;

namespace Nethermind.State.Flat.Test.Persistence.PaprikaFlat;

[TestFixture]
public class PaprikaWriteBufferTests
{
    private static readonly ValueHash256 AccountA = Path(0x11);
    private static readonly ValueHash256 AccountB = Path(0x22);
    private static readonly ValueHash256 AccountC = Path(0x33);
    private static readonly ValueHash256 AccountD = Path(0x99);
    private static readonly ValueHash256 NewAccountInDeletedRange = Path(0x1a);
    private static readonly ValueHash256 DestructedContract = Path(0x44);
    private static readonly ValueHash256 PartlyDeletedContract = Path(0x55);
    private static readonly ValueHash256 LargeContract = Path(0x66);
    private static readonly ValueHash256 SlotOne = Path(0x01);
    private static readonly ValueHash256 SlotTwo = Path(0x02);

    /// <summary>More slot writes than one 64 KiB chunk of records holds.</summary>
    private const int LargeContractSlots = 2_000;

    [Test]
    public void Range_deletes_act_on_the_root_before_the_group_and_later_writes_survive([Values] bool grouped)
    {
        using PaprikaFlatTestDb db = new();
        using (PaprikaWriteBuffer seed = new(StateId.Sync, StateId.Sync))
        {
            PaprikaFlatWriteBatch batch = new(seed);
            batch.SetAccount(AccountA, Bytes.FromHexString("0xa1"));
            batch.SetAccount(AccountB, Bytes.FromHexString("0xb1"));
            batch.SetAccount(AccountC, Bytes.FromHexString("0xc1"));
            batch.SetAccount(AccountD, Bytes.FromHexString("0xd1"));
            batch.SetStorage(DestructedContract, SlotOne, (UInt256)0x41);
            batch.SetStorage(DestructedContract, SlotTwo, (UInt256)0x42);
            batch.SetStorage(PartlyDeletedContract, SlotOne, (UInt256)0x51);
            batch.SetStorage(PartlyDeletedContract, SlotTwo, (UInt256)0x52);
            seed.Metadata.Set(Bytes.FromHexString("0x01"), Bytes.FromHexString("0x01"));
            seed.Metadata.Set(Bytes.FromHexString("0x02"), Bytes.FromHexString("0x02"));
            seed.Metadata.Set(Bytes.FromHexString("0x04"), Bytes.FromHexString("0x04"));
            db.Commit(seed);
        }

        using PaprikaWriteBuffer first = new(StateId.Sync, StateId.Sync);
        using PaprikaWriteBuffer second = new(StateId.Sync, StateId.Sync);
        Assert.That(first.IsEmpty, Is.True);

        // A batch issues its deletes before its writes; grouped, they come from a later buffer than the writes they must spare.
        PaprikaWriteBuffer deletes = grouped ? second : first;
        deletes.DeleteAccountRange(AccountA, AccountC);
        deletes.SelfDestruct(DestructedContract);
        deletes.DeleteStorageRange(PartlyDeletedContract, SlotOne, SlotOne);
        deletes.Metadata.Set(Bytes.FromHexString("0x01"), null);
        deletes.Metadata.Set(Bytes.FromHexString("0x03"), Bytes.FromHexString("0x03"));

        PaprikaFlatWriteBatch writes = new(first);
        writes.SetAccount(AccountB, Bytes.FromHexString("0xb2"));
        writes.SetAccount(NewAccountInDeletedRange, Bytes.FromHexString("0x1a"));
        writes.SetStorage(DestructedContract, SlotTwo, (UInt256)0x43);
        writes.SetStorage(PartlyDeletedContract, SlotOne, (UInt256)0x53);
        for (int slot = 0; slot < LargeContractSlots; slot++) writes.SetStorage(LargeContract, Keccak.Compute($"slot {slot}"), UInt256.MaxValue - (ulong)slot);
        first.Metadata.Set(Bytes.FromHexString("0x02"), Bytes.FromHexString("0x22"));

        if (grouped) db.Commit(first, second);
        else db.Commit(first);

        using IVisitableReadOnlyBatch reader = db.BeginReadOnlyBatch();
        UInt256 lastLargeSlot = default;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.IsEmpty, Is.False);
            Assert.That(Accounts(reader, ValueKeccak.Zero, ValueKeccak.MaxValue), Is.EqualTo(new[]
            {
                (NewAccountInDeletedRange, "0x1a"),
                (AccountB, "0xb2"),
                (AccountD, "0xd1"),
            }));
            Assert.That(Slots(reader, DestructedContract), Is.EqualTo(new[] { (SlotTwo, "0x43") }));
            Assert.That(Slots(reader, PartlyDeletedContract), Is.EqualTo(new[] { (SlotOne, "0x53"), (SlotTwo, "0x52") }));
            Assert.That(Slots(reader, LargeContract), Has.Count.EqualTo(LargeContractSlots));
            Assert.That(new PaprikaFlatReader(reader).TryGetStorage(LargeContract, Keccak.Compute($"slot {LargeContractSlots - 1}"), ref lastLargeSlot), Is.True);
            Assert.That(lastLargeSlot, Is.EqualTo(UInt256.MaxValue - (LargeContractSlots - 1)));
            Assert.That(PaprikaFlatMetadata.Read(reader).Encode(), Is.EqualTo(Bytes.FromHexString("0x0103010201220103010301040104")));
        }
    }
}
