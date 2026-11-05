// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Nethermind.State.Flat.PersistedSnapshots.Storage;

/// <summary>
/// The contiguous trie-node RLP region a base persisted snapshot occupies inside one blob
/// arena file. A base snapshot writes every RLP through a single <see cref="BlobArenaWriter"/>,
/// so its bytes form one <c>[Offset, Offset + Length)</c> run that can be prefetched in a
/// single <c>posix_fadvise(WILLNEED)</c> call.
/// </summary>
/// <remarks>
/// Only base snapshots carry a non-empty range. Compacted / CompactSized snapshots reference
/// scattered blob arenas via <c>ref_ids</c> and store <see cref="None"/>.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct BlobRange(ushort BlobArenaId, long Offset, long Length)
{
    /// <summary>Sentinel for snapshots with no contiguous blob region.</summary>
    public static readonly BlobRange None = default;

    public bool IsEmpty => Length == 0;

    /// <summary>Fixed serialized width of a range: BlobArenaId(2) + Offset(8) + Length(8).</summary>
    internal const int SerializedSize = sizeof(ushort) + sizeof(long) + sizeof(long);

    /// <summary>Serialize this range little-endian into <paramref name="span"/> (≥ <see cref="SerializedSize"/> bytes).</summary>
    internal void Write(Span<byte> span)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(span, BlobArenaId);
        BinaryPrimitives.WriteInt64LittleEndian(span[2..], Offset);
        BinaryPrimitives.WriteInt64LittleEndian(span[10..], Length);
    }

}
