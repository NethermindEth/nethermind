// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

internal static class PersistedSnapshotBloomBuilder
{
    /// <summary>
    /// Build the unified bloom for <paramref name="snapshot"/> — covers address /
    /// slot / self-destruct keys plus state-trie and storage-trie paths in a single
    /// filter. Reads bytes through the caller-owned <paramref name="session"/>; this
    /// method does not dispose it.
    /// </summary>
    internal static BloomFilter Build(WholeReadSession session, PersistedSnapshot snapshot, double bitsPerKey)
    {
        WholeReadScanner scanner = PersistedSnapshotScanner.ForWholeRead(session, snapshot);

        // Pass 1: count keys to size the bloom accurately. addrKey is the single key the reader probes for
        // the address's account, self-destruct flag, and the address half of every slot lookup, so it is
        // counted once per address — not once per account/flag/slot.
        long capacity = 0;
        foreach (WholeReadScanner.PerAddressEntry entry in scanner.PerAddresses)
        {
            capacity++; // addrKey
            foreach (WholeReadScanner.SlotEntry _ in entry.Slots)
                capacity++; // (address, slot) key
        }
        foreach (WholeReadScanner.StateNodeEntry _ in scanner.StateNodes)
            capacity++;
        foreach (WholeReadScanner.StorageNodeEntry _ in scanner.StorageNodes)
            capacity++;

        if (capacity == 0)
            capacity = 1;

        BloomFilter bloom = new(capacity, bitsPerKey);

        // Pass 2: populate. addrKey once per address (covers account, self-destruct, and the address half
        // of a slot check), then one key per slot.
        foreach (WholeReadScanner.PerAddressEntry entry in scanner.PerAddresses)
        {
            ulong addrKey = AddressKey(entry.Address);
            bloom.Add(addrKey);
            foreach (WholeReadScanner.SlotEntry slot in entry.Slots)
                bloom.Add(SlotKey(addrKey, slot.Slot));
        }
        // Trie-node keys (state + storage).
        foreach (WholeReadScanner.StateNodeEntry entry in scanner.StateNodes)
            bloom.Add(StatePathKey(entry.Path));
        foreach (WholeReadScanner.StorageNodeEntry entry in scanner.StorageNodes)
            bloom.Add(StorageNodeKey(entry.AddressHash, entry.Path));

        return bloom;
    }

    /// <summary>
    /// Bloom-key seed from the first 8 bytes of a raw 20-byte Address. Column 0x01's
    /// outer key is exactly the raw Address bytes, so the merger can read the seed
    /// directly from the outer key via
    /// <see cref="MemoryMarshal.Read{T}(ReadOnlySpan{byte})"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong AddressKey(Address address) =>
        AddressKey(address.Bytes);

    /// <summary>
    /// Span overload of <see cref="AddressKey(Address)"/> — used by the builder loop,
    /// which iterates raw 20-byte slices in a NativeMemoryList without materialising
    /// an <see cref="Address"/> object per row.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong AddressKey(scoped ReadOnlySpan<byte> addressBytes) =>
        MemoryMarshal.Read<ulong>(addressBytes);

    /// <summary>
    /// Slot bloom hash: XORs the full 32-byte big-endian slot into the address key.
    /// Serialises the <see cref="UInt256"/> once and routes through the span variant
    /// so both call sites share the exact hash bytes.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong SlotKey(ulong addressKey, in UInt256 slot)
    {
        Span<byte> slotBytes = stackalloc byte[32];
        slot.ToBigEndian(slotBytes);
        return SlotKey(addressKey, slotBytes);
    }

    /// <summary>
    /// Span-based slot bloom hash: XORs the 32-byte big-endian slot into the
    /// address key as four non-overlapping ulongs covering [0,8), [8,16),
    /// [16,24), [24,32).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong SlotKey(ulong addressKey, scoped ReadOnlySpan<byte> slot32) =>
        addressKey ^ Fold32(slot32);

    /// <summary>XOR-fold of the first 32 bytes as four non-overlapping ulongs covering
    /// [0,8), [8,16), [16,24), [24,32).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Fold32(scoped ReadOnlySpan<byte> bytes) =>
        MemoryMarshal.Read<ulong>(bytes)
        ^ MemoryMarshal.Read<ulong>(bytes[8..])
        ^ MemoryMarshal.Read<ulong>(bytes[16..])
        ^ MemoryMarshal.Read<ulong>(bytes[24..]);

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
        return Fold32(encoded) ^ encoded[32];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong StorageNodeKey(in ValueHash256 addressHash, in TreePath path) =>
        MemoryMarshal.Read<ulong>(addressHash.Bytes) ^ StatePathKey(in path);

    /// <summary>
    /// Span-based <see cref="StatePathKey(in TreePath)"/> for callers (the merger) that
    /// see raw encoded column keys rather than reconstructed <see cref="TreePath"/>s.
    /// Byte-equivalent to the <see cref="TreePath"/> overload: 4-byte and 8-byte
    /// compact keys are exactly what <c>EncodeWith4Byte</c>/<c>EncodeWith8Byte</c>
    /// produce, and the 33-byte fallback key already carries <c>[path.Path.Bytes][length]</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static ulong StatePathKey(scoped ReadOnlySpan<byte> encodedKey)
    {
        Span<byte> encoded = stackalloc byte[33];
        encoded.Clear();
        encodedKey.CopyTo(encoded);
        return Fold32(encoded) ^ encoded[32];
    }
}
