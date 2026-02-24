// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.State.Flat;

/// <summary>
/// Reference to a value stored in another persisted snapshot.
/// Used by compacted snapshots to avoid duplicating data from base snapshots.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct NodeRef(int snapshotId, int valueLengthOffset)
{
    public const int Size = 8;

    /// <summary>ID of the referenced snapshot.</summary>
    public int SnapshotId { get; } = snapshotId;

    /// <summary>Byte offset of the ValueLength LEB128 in the referenced snapshot's HSST data.</summary>
    public int ValueLengthOffset { get; } = valueLengthOffset;

    public bool IsEmpty => SnapshotId == 0 && ValueLengthOffset == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NodeRef Read(ReadOnlySpan<byte> data)
    {
        int sid = BinaryPrimitives.ReadInt32LittleEndian(data);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        return new NodeRef(sid, offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> data, in NodeRef nodeRef)
    {
        BinaryPrimitives.WriteInt32LittleEndian(data, nodeRef.SnapshotId);
        BinaryPrimitives.WriteInt32LittleEndian(data[4..], nodeRef.ValueLengthOffset);
    }
}
