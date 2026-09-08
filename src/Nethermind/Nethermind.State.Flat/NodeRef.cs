// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.State.Flat;

/// <summary>
/// Reference to a trie-node RLP stored in a blob arena file. Persisted snapshots
/// store only metadata table locally; the RLP bytes live in a separate blob arena
/// file addressed by <see cref="BlobArenaId"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct NodeRef(ushort blobArenaId, int rlpDataOffset)
{
    public const int Size = 6;

    /// <summary>
    /// ID of the blob arena <em>file</em> holding the RLP bytes (equals <c>ArenaFile.Id</c>).
    /// 16-bit, so the per-tier file count is capped at <c>ushort.MaxValue</c>; with the
    /// 2 GiB-per-file ceiling from <see cref="RlpDataOffset"/> that is ~128 TiB per tier.
    /// </summary>
    public ushort BlobArenaId { get; } = blobArenaId;

    /// <summary>
    /// File-absolute byte offset of the RLP item's first byte. Length is recovered by parsing the
    /// RLP header, so no per-entry length is stored. 32-bit caps a single blob arena file at 2 GiB
    /// (enforced by <see cref="BlobArenaWriter"/> on append).
    /// </summary>
    public int RlpDataOffset { get; } = rlpDataOffset;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static NodeRef Read(ReadOnlySpan<byte> data)
    {
        ushort id = BinaryPrimitives.ReadUInt16LittleEndian(data);
        int offset = BinaryPrimitives.ReadInt32LittleEndian(data[2..]);
        return new NodeRef(id, offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Write(Span<byte> data, in NodeRef nodeRef)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(data, nodeRef.BlobArenaId);
        BinaryPrimitives.WriteInt32LittleEndian(data[2..], nodeRef.RlpDataOffset);
    }
}
