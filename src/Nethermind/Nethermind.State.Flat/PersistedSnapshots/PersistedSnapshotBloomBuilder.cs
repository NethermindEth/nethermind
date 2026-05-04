// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

internal static class PersistedSnapshotBloomBuilder
{
    internal static BloomFilter Build(PersistedSnapshot snapshot, double bitsPerKey)
    {
        using WholeReadSession session = snapshot.BeginWholeReadSession();
        PersistedSnapshotScanner scanner = new(session, snapshot);

        // Pass 1: count keys to size the bloom accurately. Lazy entries: no decoding.
        long capacity = 0;
        foreach (PersistedSnapshotScanner.AccountEntry _ in scanner.Accounts)
            capacity++;
        foreach (PersistedSnapshotScanner.SelfDestructEntry _ in scanner.SelfDestructedStorageAddresses)
            capacity++;
        foreach (PersistedSnapshotScanner.StorageEntry _ in scanner.Storages)
            capacity += 2; // address key + (address, slot) key

        if (capacity == 0)
            capacity = 1;

        BloomFilter bloom = new(capacity, bitsPerKey);

        // Pass 2: add keys. Only Address/Slot decoded — Account/SlotValue skipped.
        foreach (PersistedSnapshotScanner.AccountEntry entry in scanner.Accounts)
            bloom.Add(AddressKey(entry.Address));

        foreach (PersistedSnapshotScanner.SelfDestructEntry entry in scanner.SelfDestructedStorageAddresses)
            bloom.Add(AddressKey(entry.Address));

        foreach (PersistedSnapshotScanner.StorageEntry entry in scanner.Storages)
        {
            ulong addrKey = AddressKey(entry.Address);
            bloom.Add(addrKey);
            bloom.Add(SlotKey(addrKey, entry.Slot));
        }

        return bloom;
    }

    /// <summary>
    /// Build a bloom filter covering the trie-node columns (state-trie paths and
    /// storage-trie (addressHash, path) keys). Sized from a scanner count pass.
    /// </summary>
    internal static BloomFilter BuildTrieBloom(PersistedSnapshot snapshot, double bitsPerKey)
    {
        using WholeReadSession session = snapshot.BeginWholeReadSession();
        PersistedSnapshotScanner scanner = new(session, snapshot);

        long capacity = 0;
        foreach (PersistedSnapshotScanner.StateNodeEntry _ in scanner.StateNodes)
            capacity++;
        foreach (PersistedSnapshotScanner.StorageNodeEntry _ in scanner.StorageNodes)
            capacity++;

        if (capacity == 0)
            capacity = 1;

        BloomFilter bloom = new(capacity, bitsPerKey);

        foreach (PersistedSnapshotScanner.StateNodeEntry entry in scanner.StateNodes)
            bloom.Add(StatePathKey(entry.Path));

        foreach (PersistedSnapshotScanner.StorageNodeEntry entry in scanner.StorageNodes)
            bloom.Add(StorageNodeKey(entry.AddressHash, entry.Path));

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

    /// <summary>
    /// Bloom key for a state-trie node, derived canonically from the path bytes and
    /// length. Independent of the on-disk column encoding so that callers (writer,
    /// merger, lookup) can all produce the same key from a <see cref="TreePath"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong StatePathKey(in TreePath path)
    {
        ReadOnlySpan<byte> pathBytes = path.Path.Bytes;
        ulong p0 = MemoryMarshal.Read<ulong>(pathBytes);
        ulong p1 = MemoryMarshal.Read<ulong>(pathBytes[8..]);
        ulong p2 = MemoryMarshal.Read<ulong>(pathBytes[16..]);
        ulong p3 = MemoryMarshal.Read<ulong>(pathBytes[24..]);
        return p0 ^ p1 ^ p2 ^ p3 ^ (ulong)path.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong StorageNodeKey(Hash256 addressHash, in TreePath path) =>
        MemoryMarshal.Read<ulong>(addressHash.Bytes) ^ StatePathKey(in path);
}
