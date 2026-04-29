// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.Storage;

namespace Nethermind.State.Flat.PersistedSnapshots;

internal static class PersistedSnapshotBloomBuilder
{
    internal static BloomFilter Build(PersistedSnapshot snapshot, double bitsPerKey)
    {
        using WholeReadSession session = snapshot.BeginWholeReadSession();
        PersistedSnapshotScanner scanner = new(session, snapshot);

        // Pass 1: count keys to size the bloom accurately.
        long capacity = 0;
        foreach (KeyValuePair<AddressAsKey, Account?> _ in scanner.Accounts)
            capacity++;
        foreach (KeyValuePair<AddressAsKey, bool> _ in scanner.SelfDestructedStorageAddresses)
            capacity++;
        foreach (KeyValuePair<(AddressAsKey, UInt256), SlotValue?> _ in scanner.Storages)
            capacity += 2; // address key + (address, slot) key

        if (capacity == 0)
            capacity = 1;

        BloomFilter bloom = new(capacity, bitsPerKey);

        // Pass 2: add keys.
        foreach (KeyValuePair<AddressAsKey, Account?> kv in scanner.Accounts)
            bloom.Add(AddressKey((Address)kv.Key));

        foreach (KeyValuePair<AddressAsKey, bool> kv in scanner.SelfDestructedStorageAddresses)
            bloom.Add(AddressKey((Address)kv.Key));

        foreach (KeyValuePair<(AddressAsKey, UInt256), SlotValue?> kv in scanner.Storages)
        {
            Address addr = (Address)kv.Key.Item1;
            ulong addrKey = AddressKey(addr);
            bloom.Add(addrKey);
            bloom.Add(SlotKey(addrKey, kv.Key.Item2));
        }

        return bloom;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong AddressKey(Address address) =>
        MemoryMarshal.Read<ulong>(address.Bytes);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong SlotKey(ulong addressKey, in UInt256 slot)
    {
        Span<byte> slotBytes = stackalloc byte[32];
        slot.ToBigEndian(slotBytes);
        ulong s0 = MemoryMarshal.Read<ulong>(slotBytes);
        ulong s1 = MemoryMarshal.Read<ulong>(slotBytes[8..]);
        ulong s2 = MemoryMarshal.Read<ulong>(slotBytes[16..]);
        ulong s3 = MemoryMarshal.Read<ulong>(slotBytes[24..]);
        return addressKey ^ s0 ^ s1 ^ s2 ^ s3;
    }
}
