// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Pbt.NodeBuffer;

/// <summary>A borrowed immutable group payload and its in-memory descendant links.</summary>
/// <remarks>The pin protecting the record must remain alive for every use of the view, including its memory and spans.</remarks>
public readonly struct PbtGroupRecord
{
    private readonly ReadOnlyMemory<byte> _record;

    internal PbtGroupRecord(ulong pointer, ReadOnlyMemory<byte> record)
    {
        Pointer = pointer;
        _record = record;
    }

    /// <summary>The buffer pointer of this record.</summary>
    public ulong Pointer { get; }

    /// <summary>The same record under <paramref name="pointer"/>, as a buffer that numbers another buffer's records exposes it.</summary>
    public PbtGroupRecord WithPointer(ulong pointer) => new(pointer, _record);

    /// <summary>The canonical path of the physical group.</summary>
    public PbtStorageNodePath Path => MemoryMarshal.AsRef<PbtGroupRecordHeader>(_record.Span).Path;

    /// <summary>The unchanged canonical group encoding, without buffer metadata.</summary>
    public ReadOnlyMemory<byte> Payload => _record[PbtGroupRecordHeader.Size..];

    /// <summary>A copy of the boundary links, for inheriting the untouched slots of a replacement group.</summary>
    public PbtGroupLinks Links => MemoryMarshal.AsRef<PbtGroupRecordHeader>(_record.Span).Links;

    /// <summary>The pointer of the first physical descendant group owned by boundary <paramref name="slot"/>, or 0 when it is not in the buffer.</summary>
    public ulong GetLink(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, PbtThreeLevelGroupGeometry.BoundarySlots);
        return MemoryMarshal.AsRef<PbtGroupRecordHeader>(_record.Span).Links[slot];
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PbtGroupRecordHeader
{
    public static readonly int Size = Unsafe.SizeOf<PbtGroupRecordHeader>();

    public int PayloadLength;
    public PbtStorageNodePath Path;
    public PbtGroupLinks Links;
}
