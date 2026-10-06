// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.ZkEvm.Test.Extensions;

/// <summary>The hashes an <see cref="Address"/> keeps on the guest, and the storage-cell hash that extends them.</summary>
/// <remarks>An AES-capable host hashes the public members the shared way, so the scalar sums are called directly too.</remarks>
// The slot memo is process-wide, and a case reseeds.
[NonParallelizable]
public class GuestAddressHashTests
{
    private static readonly byte[] AddressBytes = Bytes.FromHexString("0x5a4eab120fb44eb6684e5e32785702ff45ea344d");
    private static readonly byte[] SlotBytes = Bytes.FromHexString("0x290decd9548b62a8d60345a988386fc84ba6bc95484008f6362f93160ef3e563");
    private static readonly UInt256 OtherSeed = new(0x219B4AD604915E33UL, 0x28811B0595AE539EUL,
        0x5D38E6AFF0752500UL, 0xC8AEAC7F08A75C3DUL);

    [TearDown]
    public void RestoreSeed() => SpanExtensions.SeedHashes(SeedGuestHashes.Seed);

    [Test]
    public void Address_hash_agrees_with_the_value_address_and_key_hashes()
    {
        Random random = new(42);
        byte[] bytes = new byte[Address.Size];
        for (int i = 0; i < 256; i++)
        {
            random.NextBytes(bytes);
            Address address = new(bytes);
            int expected = new ValueAddress(bytes).GetHashCode();
            ref byte start = ref MemoryMarshal.GetArrayDataReference(bytes);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(address.GetHashCode(), Is.EqualTo(expected), "computed");
                Assert.That(address.GetHashCode(), Is.EqualTo(expected), "kept");
                Assert.That(new AddressAsKey(address).GetHashCode(), Is.EqualTo(expected), "as a key");
                Assert.That(unchecked((int)new AddressAsKey(address).GetHashCode64()), Is.EqualTo(expected), "as a 64-bit key");
                Assert.That((long)SpanExtensions.FinalizeAddressSum(SpanExtensions.SumAddressWords(ref start)),
                    Is.EqualTo(SpanExtensions.FastHash64For20BytesFallback(ref start)), "kept sum against the scalar hash");
            }
        }
    }

    [Test]
    public void Slot_hash_depends_on_every_address_and_slot_byte([Range(0, Address.Size + 31)] int position)
    {
        byte[] address = (byte[])AddressBytes.Clone();
        byte[] slot = (byte[])SlotBytes.Clone();
        ulong original = MixSlot(address, slot);

        if (position < Address.Size) address[position] ^= 0x01;
        else slot[position - Address.Size] ^= 0x01;

        Assert.That(MixSlot(address, slot), Is.Not.EqualTo(original));
    }

    [Test]
    public void Slot_hash_memo_answers_only_what_the_current_seed_computes([Values] bool zeroKey)
    {
        // A fresh seed leaves the memo holding the all-zero key.
        ulong addressSum = zeroKey ? 0 : SpanExtensions.SumAddressWords(ref MemoryMarshal.GetArrayDataReference(AddressBytes));
        byte[] slot = zeroKey ? new byte[32] : SlotBytes;

        SpanExtensions.SeedHashes(OtherSeed);
        ulong otherSeedHash = MixSlot(addressSum, slot);
        SpanExtensions.SeedHashes(SeedGuestHashes.Seed);
        ulong afterReseed = MixSlot(addressSum, slot);
        MixSlot(~addressSum, slot);
        ulong computed = MixSlot(addressSum, slot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterReseed, Is.EqualTo(computed));
            Assert.That(computed, Is.Not.EqualTo(otherSeedHash));
            Assert.That(MixSlot(addressSum, slot), Is.EqualTo(computed), "repeated");
        }
    }

    [Test]
    public void Slot_index_hash_reuses_the_memo_only_for_the_last_slot([Values] bool zeroKey)
    {
        // A fresh seed leaves the memo holding the all-zero key.
        byte[] slot = zeroKey ? new byte[32] : SlotBytes;
        byte[] otherSlot = (byte[])SlotBytes.Clone();
        otherSlot[^1] ^= 0x01;
        ulong addressSum = SpanExtensions.SumAddressWords(ref MemoryMarshal.GetArrayDataReference(AddressBytes));

        SpanExtensions.SeedHashes(SeedGuestHashes.Seed);
        ulong afterReseed = MixSlotIndex(slot);
        MixSlot(addressSum, otherSlot);
        ulong computed = MixSlotIndex(slot);
        MixSlot(~addressSum, slot);
        ulong remembered = MixSlotIndex(slot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterReseed, Is.EqualTo(computed), "after reseed");
            Assert.That(remembered, Is.EqualTo(computed), "remembered");
            Assert.That(MixSlotIndex(otherSlot), Is.Not.EqualTo(computed), "other slot");
        }
    }

    [Test]
    public void Slot_index_hash_misses_the_memo_when_any_lane_differs([Range(0, 3)] int lane)
    {
        byte[] laneSlot = (byte[])SlotBytes.Clone();
        laneSlot[lane * sizeof(ulong)] ^= 0x01;
        ulong addressSum = SpanExtensions.SumAddressWords(ref MemoryMarshal.GetArrayDataReference(AddressBytes));

        SpanExtensions.SeedHashes(SeedGuestHashes.Seed);
        ulong computed = MixSlotIndex(laneSlot);
        MixSlot(addressSum, SlotBytes);

        Assert.That(MixSlotIndex(laneSlot), Is.EqualTo(computed));
    }

    private static ulong MixSlotIndex(byte[] slot) => SpanExtensions.MixSlotIndex(ref MemoryMarshal.GetArrayDataReference(slot));

    [Test]
    public void Storage_cell_hash_is_its_address_and_slot_hash()
    {
        UInt256 slot = new(SlotBytes, isBigEndian: true);
        StorageCell cell = new(new Address(AddressBytes), in slot);
        StorageCell equalCell = new(new Address(AddressBytes), in slot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cell.GetHashCode64(), Is.EqualTo(cell.Address.GetHashCode64(in slot)));
            Assert.That(equalCell.GetHashCode64(), Is.EqualTo(cell.GetHashCode64()), "equal cell");
            Assert.That(new StorageCell(cell.Address, slot + 1).GetHashCode64(), Is.Not.EqualTo(cell.GetHashCode64()), "other slot");
        }
    }

    [Test]
    public void Storage_cell_hash_uses_the_seed_active_when_it_is_made()
    {
        UInt256 slot = new(SlotBytes, isBigEndian: true);
        long defaultSeedHash = new StorageCell(new Address(AddressBytes), in slot).GetHashCode64();

        SpanExtensions.SeedHashes(OtherSeed);
        StorageCell cell = new(new Address(AddressBytes), in slot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cell.GetHashCode64(), Is.EqualTo(cell.Address.GetHashCode64(in slot)));
            Assert.That(cell.GetHashCode64(), Is.Not.EqualTo(defaultSeedHash));
        }
    }

    private static ulong MixSlot(byte[] address, byte[] slot) =>
        MixSlot(SpanExtensions.SumAddressWords(ref MemoryMarshal.GetArrayDataReference(address)), slot);

    private static ulong MixSlot(ulong addressSum, byte[] slot) =>
        SpanExtensions.MixSlot(addressSum, ref MemoryMarshal.GetArrayDataReference(slot));
}
