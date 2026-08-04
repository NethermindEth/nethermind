// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

/// <summary>
/// Covers the two preimage storage key shapes. In preimage mode the address is not hashed, so whatever leads the
/// key is attacker-chosen: the 4-byte lead of <see cref="FlatLayout.PreimageFlatV1"/> is cheap to mine and vanity
/// addresses really do share one (all Seaport contracts lead with zero bytes), which turns a per-account slot
/// scan into a scan of every account sharing the lead.
/// </summary>
[TestFixture]
public class PreimageStorageKeyTests
{
    private const int SlotsPerAccount = 4;

    // Two addresses sharing a mined 4-byte lead.
    private static readonly Address AddressA = new("0x00000000aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private static readonly Address AddressB = new("0x00000000bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");

    private const string SlotOneKey = "0000000000000000000000000000000000000000000000000000000000000001";
    private const string AddressALead = "00000000";
    private const string AddressARest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [TestCase(FlatLayout.PreimageFlat, AddressALead + AddressARest + SlotOneKey)]
    [TestCase(FlatLayout.PreimageFlatV1, AddressALead + SlotOneKey + AddressARest)]
    public void StorageKey_HasExpectedShape(FlatLayout layout, string expectedKey)
    {
        using SnapshotableMemDb state = new();
        using SnapshotableMemDb storage = new();
        WriteSlots(state, storage, AddressA, layout, slotCount: 1);

        Assert.That(storage.FirstKey, Is.EqualTo(Bytes.FromHexString(expectedKey)));
    }

    [TestCase(FlatLayout.PreimageFlat, SlotsPerAccount)]
    [TestCase(FlatLayout.PreimageFlatV1, SlotsPerAccount * 2)]
    public void StorageScan_WalksOnlyOwnAccountSlots(FlatLayout layout, int expectedKeysWalked)
    {
        using SnapshotableMemDb state = new();
        using SnapshotableMemDb storage = new();
        WriteSlots(state, storage, AddressA, layout, SlotsPerAccount);
        WriteSlots(state, storage, AddressB, layout, SlotsPerAccount);

        CountingSortedStore countingStorage = new(storage);
        BaseFlatPersistence.Reader reader = new(
            state,
            countingStorage,
            isPreimageMode: true,
            rlpWrapSlots: true,
            fullAddressStorageKey: IsFullAddressKey(layout));

        List<UInt256> scannedSlots = [];
        using (IPersistence.IFlatIterator iterator = reader.CreateStorageIterator(AsPreimageKey(AddressA), ValueKeccak.Zero, ValueKeccak.MaxValue))
        {
            while (iterator.MoveNext()) scannedSlots.Add(new UInt256(iterator.CurrentKey.Bytes, isBigEndian: true));
        }

        using (Assert.EnterMultipleScope())
        {
            // Both shapes yield the same slots — only the shape decides how much of the column has to be read
            // to get them, which is what makes a scan over vanity-prefixed accounts quadratic under V1.
            Assert.That(scannedSlots, Is.EqualTo(Slots(SlotsPerAccount)));
            Assert.That(countingStorage.KeysWalked, Is.EqualTo(expectedKeysWalked));
        }
    }

    private static bool IsFullAddressKey(FlatLayout layout) => layout is not FlatLayout.PreimageFlatV1;

    private static List<UInt256> Slots(int slotCount)
    {
        List<UInt256> slots = [];
        for (int i = 1; i <= slotCount; i++) slots.Add((UInt256)i);
        return slots;
    }

    private static void WriteSlots(SnapshotableMemDb state, SnapshotableMemDb storage, Address address, FlatLayout layout, int slotCount)
    {
        using IWriteBatch stateBatch = state.StartWriteBatch();
        using IWriteBatch storageBatch = storage.StartWriteBatch();
        BaseFlatPersistence.WriteBatch writeBatch = new(
            state,
            storage,
            stateBatch,
            storageBatch,
            WriteFlags.None,
            rlpWrapSlots: true,
            fullAddressStorageKey: IsFullAddressKey(layout));

        foreach (UInt256 slot in Slots(slotCount))
        {
            writeBatch.SetStorage(AsPreimageKey(address), AsPreimageKey(slot), SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString("0x01")));
        }
    }

    /// <summary>Preimage mode fakes the address hash by copying the address into the leading bytes.</summary>
    private static ValueHash256 AsPreimageKey(Address address)
    {
        ValueHash256 key = ValueKeccak.Zero;
        address.Bytes.CopyTo(key.BytesAsSpan);
        return key;
    }

    /// <summary>Preimage mode fakes the slot hash with the big-endian slot index.</summary>
    private static ValueHash256 AsPreimageKey(in UInt256 slot)
    {
        ValueHash256 key = ValueKeccak.Zero;
        slot.ToBigEndian(key.BytesAsSpan);
        return key;
    }

    /// <summary>Counts how many keys a range scan actually walks, including the ones it filters back out.</summary>
    private sealed class CountingSortedStore(ISortedKeyValueStore inner) : ISortedKeyValueStore
    {
        public int KeysWalked { get; private set; }

        public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => inner.Get(key, flags);
        public byte[]? FirstKey => inner.FirstKey;
        public byte[]? LastKey => inner.LastKey;

        public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive) =>
            new CountingSortedView(this, inner.GetViewBetween(firstKeyInclusive, lastKeyExclusive));

        private sealed class CountingSortedView(CountingSortedStore owner, ISortedView inner) : ISortedView
        {
            public bool StartBefore(ReadOnlySpan<byte> value) => inner.StartBefore(value);

            public bool MoveNext()
            {
                if (!inner.MoveNext()) return false;
                owner.KeysWalked++;
                return true;
            }

            public ReadOnlySpan<byte> CurrentKey => inner.CurrentKey;
            public ReadOnlySpan<byte> CurrentValue => inner.CurrentValue;

            public void Dispose() => inner.Dispose();
        }
    }
}
