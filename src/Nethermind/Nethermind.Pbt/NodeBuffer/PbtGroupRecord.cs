// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;

namespace Nethermind.Pbt.NodeBuffer;

/// <summary>A group record held in place inside a buffer of the group record buffer.</summary>
/// <remarks>Buffer records borrow native storage for the lifetime of the pin protecting them, including their memory and spans.</remarks>
public readonly struct PbtGroupRecord(ReadOnlyMemory<byte> buffer, int offset)
{
    private readonly ReadOnlyMemory<byte> _buffer = buffer;
    private readonly int _offset = offset;

    /// <summary>Whether this references no record.</summary>
    public bool IsNull => _buffer.Equals(default);

    /// <summary>The whole record: header and payload.</summary>
    public ReadOnlySpan<byte> Span
    {
        get
        {
            ReadOnlySpan<byte> record = _buffer.Span[_offset..];
            return record[..PbtGroupRecordCodec.Length(record)];
        }
    }

    public PbtGroupRecordKind Kind => (PbtGroupRecordKind)_buffer.Span[_offset];

    /// <summary>The canonical path of the physical group.</summary>
    public PbtStorageNodePath Path => Header.Path;

    /// <summary>The unchanged canonical group encoding, without buffer metadata.</summary>
    public ReadOnlyMemory<byte> Payload => _buffer.Slice(_offset + PbtGroupRecordCodec.HeaderLength, Header.PayloadLength);

    /// <summary>A copy of the boundary links, for inheriting the untouched slots of a replacement group.</summary>
    public PbtGroupLinks Links => Header.Links;

    private ref readonly PbtGroupRecordHeader Header => ref MemoryMarshal.AsRef<PbtGroupRecordHeader>(_buffer.Span[_offset..]);

    /// <summary>The pointer of the first physical descendant group owned by boundary <paramref name="slot"/>, or 0 when it is not in the buffer.</summary>
    public ulong GetLink(int slot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, PbtTwoLevelGroupGeometry.BoundarySlots);
        return Header.Links[slot];
    }
}
