// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Trie.NodeBuffer;

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>Encodes the root key records of the <see cref="NodeRecordBuffer"/> root index.</summary>
/// <remarks>
/// A root key record is <see cref="NodeRecordCodec.RootKeyLength"/> bytes: its kind, then the address hash (zero for the state trie),
/// the root hash, the pointer of the root's record and the pointer of the previous root key record of the same index slot.
/// </remarks>
internal static class RootKeyRecordCodec
{
    private const int AddressOffset = 8;
    private const int RootOffset = 40;
    private const int RootPointerOffset = 72;
    private const int PreviousOffset = 80;

    /// <summary>Writes the root key record of <paramref name="root"/> into the zero-filled <paramref name="record"/>.</summary>
    /// <param name="address">The storage trie's address hash, or null for the state trie.</param>
    public static void Write(Span<byte> record, Hash256? address, in ValueHash256 root, ulong rootPointer, ulong previous)
    {
        record[0] = (byte)NodeRecordKind.RootKey;
        address?.Bytes.CopyTo(record[AddressOffset..]);
        root.Bytes.CopyTo(record[RootOffset..]);
        BinaryPrimitives.WriteUInt64LittleEndian(record[RootPointerOffset..], rootPointer);
        BinaryPrimitives.WriteUInt64LittleEndian(record[PreviousOffset..], previous);
    }

    /// <summary>Whether <paramref name="record"/> indexes <paramref name="root"/> of the trie of <paramref name="addressHash"/>.</summary>
    /// <param name="addressHash">The storage trie's address hash, or zero for the state trie.</param>
    public static bool Matches(ReadOnlySpan<byte> record, in ValueHash256 addressHash, in ValueHash256 root) =>
        MemoryMarshal.Read<ValueHash256>(record[AddressOffset..]) == addressHash
        && MemoryMarshal.Read<ValueHash256>(record[RootOffset..]) == root;

    /// <summary>The pointer of the root's record.</summary>
    public static ulong RootPointer(ReadOnlySpan<byte> record) => BinaryPrimitives.ReadUInt64LittleEndian(record[RootPointerOffset..]);

    /// <summary>The pointer of the previous root key record of the same index slot, or zero when there is none.</summary>
    public static ulong Previous(ReadOnlySpan<byte> record) => BinaryPrimitives.ReadUInt64LittleEndian(record[PreviousOffset..]);
}
