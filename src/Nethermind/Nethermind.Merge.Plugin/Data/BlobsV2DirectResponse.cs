// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Json;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>Writes blob/proof V2 results directly into a <see cref="PipeWriter"/>.</summary>
public sealed class BlobsV2DirectResponse : IStreamableResult, IReadOnlyList<BlobAndProofV2?>, IDisposable
{
    private readonly ArrayPoolList<byte[]?> _blobs;
    private readonly ArrayPoolList<ReadOnlyMemory<byte[]>> _proofs;
    private readonly int _count;

    public BlobsV2DirectResponse(ArrayPoolList<byte[]?> blobs, ArrayPoolList<ReadOnlyMemory<byte[]>> proofs, int count)
    {
        Debug.Assert(count <= blobs.Count && count <= proofs.Count, "count must not exceed list lengths");
        _blobs = blobs;
        _proofs = proofs;
        _count = count;
    }

    public BlobsV2DirectResponse(byte[]?[] blobs, ReadOnlyMemory<byte[]>[] proofs, int count)
        : this(new ArrayPoolList<byte[]?>(blobs.Length, blobs), new ArrayPoolList<ReadOnlyMemory<byte[]>>(proofs.Length, proofs), count)
    {
    }

    public int Count => _count;

    public BlobAndProofV2? this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count, nameof(index));
            return BuildBlobAndProofV2(index);
        }
    }

    public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken) =>
        StreamableResultWriter.WriteArrayAsync(writer, _count,
            new ItemWriter(_blobs.UnsafeGetInternalArray(), _proofs.UnsafeGetInternalArray()), cancellationToken);

    IEnumerator<BlobAndProofV2?> IEnumerable<BlobAndProofV2?>.GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
        {
            yield return BuildBlobAndProofV2(i);
        }
    }

    private BlobAndProofV2? BuildBlobAndProofV2(int i) =>
        _blobs[i] is { } blob ? new BlobAndProofV2(blob, _proofs[i].ToArray()) : null;

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<BlobAndProofV2?>)this).GetEnumerator();

    public void Dispose()
    {
        _blobs.Dispose();
        _proofs.Dispose();
    }

    private readonly struct ItemWriter(byte[]?[] blobs, ReadOnlyMemory<byte[]>[] proofsByBlob) : IJsonArrayItemWriter
    {
        public void WriteItem(PipeWriter writer, int index)
        {
            byte[]? blob = blobs[index];
            if (blob is null)
            {
                writer.Write("null"u8);
                return;
            }

            writer.Write("{\"blob\":"u8);
            HexWriter.WriteHexString(writer, blob, chunked: true);
            writer.Write(",\"proofs\":["u8);

            ReadOnlySpan<byte[]> proofs = proofsByBlob[index].Span;
            for (int p = 0; p < proofs.Length; p++)
            {
                if (p > 0) writer.Write(","u8);
                HexWriter.WriteHexString(writer, proofs[p], chunked: false);
            }

            writer.Write("]}"u8);
        }
    }
}
