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
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>Writes blob/proof V2 results directly into a <see cref="PipeWriter"/>.</summary>
public sealed class BlobsV2DirectResponse : IStreamableResult, IReadOnlyList<BlobAndProofV2?>
{
    private readonly byte[]?[] _blobs;
    private readonly ReadOnlyMemory<byte[]>[] _proofs;
    private readonly int _count;

    public BlobsV2DirectResponse(byte[]?[] blobs, ReadOnlyMemory<byte[]>[] proofs, int count)
    {
        Debug.Assert(count <= blobs.Length && count <= proofs.Length, "count must not exceed array lengths");
        _blobs = blobs;
        _proofs = proofs;
        _count = count;
    }

    public int Count => _count;

    public BlobAndProofV2? this[int index]
    {
        get
        {
            if ((uint)index >= (uint)_count) throw new ArgumentOutOfRangeException(nameof(index));
            return BuildBlobAndProofV2(index);
        }
    }

    public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken) =>
        StreamableResultWriter.WriteArrayAsync(writer, _count, new ItemWriter(_blobs, _proofs), cancellationToken);

    // Explicit interface implementation: only used by tests via IEnumerable<T> cast.
    // Production serialization goes through IStreamableResult.WriteToAsync.
    IEnumerator<BlobAndProofV2?> IEnumerable<BlobAndProofV2?>.GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
        {
            yield return BuildBlobAndProofV2(i);
        }
    }

    private BlobAndProofV2? BuildBlobAndProofV2(int i)
    {
        byte[]? blob = _blobs[i];
        return blob is null ? null : new BlobAndProofV2(blob, _proofs[i].ToArray());
    }

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<BlobAndProofV2?>)this).GetEnumerator();

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
            PayloadBodiesDirectResponseWriter.WriteHexString(writer, blob, chunked: true);
            writer.Write(",\"proofs\":["u8);

            ReadOnlySpan<byte[]> proofs = proofsByBlob[index].Span;
            for (int p = 0; p < proofs.Length; p++)
            {
                if (p > 0) writer.Write(","u8);
                PayloadBodiesDirectResponseWriter.WriteHexString(writer, proofs[p], chunked: false);
            }

            writer.Write("]}"u8);
        }
    }
}
