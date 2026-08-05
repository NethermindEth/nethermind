// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Flat.History;

/// <summary>
/// Block-major, chunked store for the changesets sidecar (<c>[block BE | chunkIndex BE] -&gt; payload</c>). A
/// block's changeset may be split across multiple chunks so a single block never forces an unbounded wire message
/// (39-2) or in-memory buffer (39-3); chunk index is 0-based and contiguous per block, so a reader can detect a
/// missing chunk as a gap in the sequence rather than needing an explicit chunk count up front.
/// </summary>
internal sealed class ChangesetSidecarStore
{
    private const int BlockBytes = sizeof(ulong);
    private const int ChunkIndexBytes = sizeof(uint);
    private const int KeyLength = BlockBytes + ChunkIndexBytes;

    // ~1MB cap per chunk value, matching the wire protocol's bounded-message assumption (39-2's HistoryServer)
    // and the backfill importer's bounded-buffer assumption (39-3, IWindowImportSource). A block whose encoded
    // changeset exceeds this splits across multiple contiguous chunk indices instead of writing one oversized
    // value - a destruct-heavy block is exactly the shape that needs this (see HistoryWriter's v3 self-destruct
    // handling, which can enumerate up to DestructSlotEnumerationCap slots for a single account).
    internal const int MaxChunkPayloadBytes = 1024 * 1024;

    private readonly IDb _sidecar;

    public ChangesetSidecarStore(IDb sidecar)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        _sidecar = sidecar;
    }

    /// <summary>
    /// Splits <paramref name="payload"/> into contiguous, 0-based <see cref="MaxChunkPayloadBytes"/>-sized chunks
    /// and writes each one, so a large block's encoded changeset never produces one oversized value. An empty
    /// payload still writes a single (empty) chunk 0, distinguishing "recorded, no changes" from "never recorded".
    /// </summary>
    public void RecordChangeset(ulong block, ReadOnlySpan<byte> payload, IWriteBatch batch)
    {
        if (payload.Length == 0)
        {
            RecordChunk(block, 0, ReadOnlySpan<byte>.Empty, batch);
            return;
        }

        uint chunkIndex = 0;
        int offset = 0;
        while (offset < payload.Length)
        {
            int length = Math.Min(MaxChunkPayloadBytes, payload.Length - offset);
            RecordChunk(block, chunkIndex, payload.Slice(offset, length), batch);
            offset += length;
            chunkIndex++;
        }
    }

    public void RecordChunk(ulong block, uint chunkIndex, ReadOnlySpan<byte> payload, IWriteBatch batch)
    {
        Span<byte> key = stackalloc byte[KeyLength];
        WriteKey(key, block, chunkIndex);

        // PutSpan's default implementation treats an empty span as null (IsNull, not IsEmpty) and removes the key
        // instead of writing it - the same reason StorageClearStore's empty clear marker uses Set directly. An
        // explicit empty chunk must still round-trip as present, not absent, so TryGetChunk can tell "recorded, no
        // changes" from "never recorded".
        if (payload.IsEmpty)
        {
            batch.Set(key, Array.Empty<byte>());
            return;
        }

        batch.PutSpan(key, payload);
    }

    public byte[]? TryGetChunk(ulong block, uint chunkIndex)
    {
        Span<byte> key = stackalloc byte[KeyLength];
        WriteKey(key, block, chunkIndex);
        return _sidecar.Get(key);
    }

    private static void WriteKey(Span<byte> destination, ulong block, uint chunkIndex)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, block);
        BinaryPrimitives.WriteUInt32BigEndian(destination[BlockBytes..], chunkIndex);
    }
}
