// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.NodeBuffer;

/// <summary>Encodes the root key records of the <see cref="PbtGroupRecordBuffer"/> root index.</summary>
/// <remarks>
/// A root key record is <see cref="PbtGroupRecordCodec.RootKeyLength"/> bytes: its kind, then the root hash, the pointer of the
/// root's record and the pointer of the previous root key record of the same index slot.
/// </remarks>
public static class PbtRootKeyRecordCodec
{
    private const int RootOffset = 8;
    private const int RootPointerOffset = 40;
    private const int PreviousOffset = 48;

    /// <summary>Writes the root key record of <paramref name="root"/> into the zero-filled <paramref name="record"/>.</summary>
    public static void Write(Span<byte> record, in ValueHash256 root, ulong rootPointer, ulong previous)
    {
        record[0] = (byte)PbtGroupRecordKind.RootKey;
        root.Bytes.CopyTo(record[RootOffset..]);
        BinaryPrimitives.WriteUInt64LittleEndian(record[RootPointerOffset..], rootPointer);
        BinaryPrimitives.WriteUInt64LittleEndian(record[PreviousOffset..], previous);
    }

    /// <summary>Whether <paramref name="record"/> indexes <paramref name="root"/>.</summary>
    public static bool Matches(ReadOnlySpan<byte> record, in ValueHash256 root) => MemoryMarshal.Read<ValueHash256>(record[RootOffset..]) == root;

    /// <summary>The pointer of the root's record.</summary>
    public static ulong RootPointer(ReadOnlySpan<byte> record) => BinaryPrimitives.ReadUInt64LittleEndian(record[RootPointerOffset..]);

    /// <summary>The pointer of the previous root key record of the same index slot, or zero when there is none.</summary>
    public static ulong Previous(ReadOnlySpan<byte> record) => BinaryPrimitives.ReadUInt64LittleEndian(record[PreviousOffset..]);
}
