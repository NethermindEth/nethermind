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

        // Pass 2: add keys. Only AddressHash/Slot decoded — Account/SlotValue skipped.
        foreach (PersistedSnapshotScanner.AccountEntry entry in scanner.Accounts)
            bloom.Add(AddressKey(entry.AddressHash));

        foreach (PersistedSnapshotScanner.SelfDestructEntry entry in scanner.SelfDestructedStorageAddresses)
            bloom.Add(AddressKey(entry.AddressHash));

        foreach (PersistedSnapshotScanner.StorageEntry entry in scanner.Storages)
        {
            ulong addrKey = AddressKey(entry.AddressHash);
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
    internal static ulong AddressKey(in ValueHash256 addressHash) =>
        MemoryMarshal.Read<ulong>(addressHash.Bytes);

    /// <summary>
    /// Hashes the leading 30 bytes of the big-endian slot (the slot-prefix bucket
    /// used as the outer HSST key). The trailing 2-byte suffix is intentionally
    /// dropped — bloom checks only the prefix bucket. Writer-side adds go through
    /// <see cref="SlotPrefixKey"/> with the prefix bytes already in hand.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong SlotKey(ulong addressKey, in UInt256 slot)
    {
        Span<byte> slotBytes = stackalloc byte[32];
        slot.ToBigEndian(slotBytes);
        return SlotPrefixKey(addressKey, slotBytes[..30]);
    }

    /// <summary>
    /// Writer-side slot bloom hash: XORs the 30-byte slot prefix into the address
    /// key. Reads four ulongs covering bytes [0,8), [8,16), [16,24), [22,30); the
    /// last read is masked to zero its low 2 bytes so bytes 22-23 don't double-XOR
    /// against the third read (they'd cancel).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong SlotPrefixKey(ulong addressKey, scoped ReadOnlySpan<byte> slotPrefix30)
    {
        ulong s0 = MemoryMarshal.Read<ulong>(slotPrefix30);
        ulong s1 = MemoryMarshal.Read<ulong>(slotPrefix30[8..]);
        ulong s2 = MemoryMarshal.Read<ulong>(slotPrefix30[16..]);
        ulong s3 = MemoryMarshal.Read<ulong>(slotPrefix30[22..]) & 0xFFFF_FFFF_FFFF_0000ul;
        return addressKey ^ s0 ^ s1 ^ s2 ^ s3;
    }

    /// <summary>
    /// Bloom key for a state-trie node, hashed from the same encoded byte-sequence
    /// that the writer stores on disk (4-byte form for length 0–7, 8-byte for 8–15,
    /// 33-byte fallback for 16+). Routing through the encoding makes the key
    /// independent of whether the <see cref="TreePath"/> arrived canonical or with a
    /// non-zero tail, and matches the path the scanner reconstructs on reload.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong StatePathKey(in TreePath path)
    {
        Span<byte> encoded = stackalloc byte[33];
        int length = path.Length;
        if (length < 8)
            path.EncodeWith4Byte(encoded[..4]);
        else if (length < 16)
            path.EncodeWith8Byte(encoded[..8]);
        else
        {
            path.Path.Bytes.CopyTo(encoded);
            encoded[32] = (byte)length;
        }
        ulong p0 = MemoryMarshal.Read<ulong>(encoded);
        ulong p1 = MemoryMarshal.Read<ulong>(encoded[8..]);
        ulong p2 = MemoryMarshal.Read<ulong>(encoded[16..]);
        ulong p3 = MemoryMarshal.Read<ulong>(encoded[24..]);
        return p0 ^ p1 ^ p2 ^ p3 ^ encoded[32];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong StorageNodeKey(in ValueHash256 addressHash, in TreePath path) =>
        MemoryMarshal.Read<ulong>(addressHash.Bytes) ^ StatePathKey(in path);
}
