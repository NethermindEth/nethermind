// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.State.Flat;

/// <summary>
/// Reference to a trie-node RLP stored in a blob arena file. Persisted snapshots
/// store only metadata HSST locally; the RLP bytes live in a separate blob arena
/// file addressed by <see cref="BlobArenaId"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct NodeRef(ushort blobArenaId, int rlpDataOffset)
{
    public const int Size = 6;

    /// <summary>
    /// ID of the blob arena <em>file</em> that holds the RLP bytes — equals the
    /// underlying <c>ArenaFile.Id</c>. Many writers across many base snapshots
    /// append into the same file, so the id alone is not enough to locate the
    /// value: <see cref="RlpDataOffset"/> is the file-absolute offset. 16-bit:
    /// per-tier file count is capped at <c>ushort.MaxValue</c> (65 535) files.
    /// Combined with the 2 GiB-per-file ceiling enforced by
    /// <see cref="RlpDataOffset"/>, total per-tier capacity is ~128 TiB.
    /// </summary>
    public ushort BlobArenaId { get; } = blobArenaId;

    /// <summary>
    /// File-absolute byte offset of the RLP item's first byte within the blob arena
    /// file. Length is recovered by parsing the RLP header (see
    /// <c>RlpHelpers.PeekNextRlpLength</c>), so the index does not carry per-entry
    /// value-length metadata.
    ///
    /// 32-bit caps a single blob arena file at 2 GiB. <see cref="BlobArenaWriter"/>
    /// enforces this on append; <see cref="BlobArenaManager.CreateWriter"/> picks
    /// a fresh file when the estimate exceeds the current file's headroom.
    /// </summary>
    public int RlpDataOffset { get; } = rlpDataOffset;

    public bool IsEmpty => BlobArenaId == 0 && RlpDataOffset == 0;

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
