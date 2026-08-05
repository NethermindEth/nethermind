// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Generic;
using System.Threading;
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

    private readonly IDb _sidecar;

    public ChangesetSidecarStore(IDb sidecar)
    {
        ArgumentNullException.ThrowIfNull(sidecar);
        _sidecar = sidecar;
    }

    public void RecordChunk(ulong block, uint chunkIndex, ReadOnlySpan<byte> payload, IWriteBatch batch)
    {
        Span<byte> key = stackalloc byte[KeyLength];
        WriteKey(key, block, chunkIndex);
        batch.PutSpan(key, payload);
    }

    public byte[]? TryGetChunk(ulong block, uint chunkIndex)
    {
        Span<byte> key = stackalloc byte[KeyLength];
        WriteKey(key, block, chunkIndex);
        return _sidecar.Get(key);
    }

    public List<(ulong Block, uint ChunkIndex, byte[] Payload)> ScanRange(ulong fromBlockInclusive, ulong toBlockInclusive, long byteLimit, int maxChunks, CancellationToken cancellationToken)
    {
        List<(ulong, uint, byte[])> results = [];
        if (_sidecar is not ISortedKeyValueStore sorted) return results;

        byte[] lowerBound = new byte[KeyLength];
        WriteKey(lowerBound, fromBlockInclusive, 0);

        byte[] upperBound = new byte[KeyLength + 1];
        WriteKey(upperBound, toBlockInclusive, uint.MaxValue);

        long consumed = 0;
        using ISortedView view = sorted.GetViewBetween(lowerBound, upperBound);
        while (consumed < byteLimit && results.Count < maxChunks && !cancellationToken.IsCancellationRequested && view.MoveNext())
        {
            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length != KeyLength) continue;

            ulong block = BinaryPrimitives.ReadUInt64BigEndian(key);
            uint chunkIndex = BinaryPrimitives.ReadUInt32BigEndian(key[BlockBytes..]);
            byte[] payload = view.CurrentValue.ToArray();
            results.Add((block, chunkIndex, payload));
            consumed += payload.Length;
        }

        return results;
    }

    private static void WriteKey(Span<byte> destination, ulong block, uint chunkIndex)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, block);
        BinaryPrimitives.WriteUInt32BigEndian(destination[BlockBytes..], chunkIndex);
    }
}
