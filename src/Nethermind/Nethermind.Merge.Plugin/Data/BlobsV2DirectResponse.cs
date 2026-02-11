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
using Nethermind.Serialization.Json;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Wraps parallel arrays of blobs and proofs and writes JSON directly into a
/// <see cref="PipeWriter"/>, bypassing <see cref="System.Text.Json.Utf8JsonWriter"/>
/// to avoid extra buffer copies for large blob payloads.
/// </summary>
public sealed class BlobsV2DirectResponse : IStreamableResult, IEnumerable<BlobAndProofV2?>
{
    private readonly byte[]?[] _blobs;
    private readonly ReadOnlyMemory<byte[]>[] _proofs;
    private readonly int _count;

    public BlobsV2DirectResponse(byte[]?[] blobs, ReadOnlyMemory<byte[]>[] proofs, int count)
    {
        Debug.Assert(count <= blobs.Length && count <= proofs.Length,
            "count must not exceed array lengths");
        _blobs = blobs;
        _proofs = proofs;
        _count = count;
    }

    public async ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        writer.Write("["u8);

        for (int i = 0; i < _count; i++)
        {
            if (i > 0) writer.Write(","u8);

            byte[]? blob = _blobs[i];
            if (blob is null)
            {
                writer.Write("null"u8);
            }
            else
            {
                writer.Write("{\"blob\":\"0x"u8);
                HexWriter.WriteHexChunked(writer, blob);
                writer.Write("\",\"proofs\":["u8);

                ReadOnlySpan<byte[]> proofs = _proofs[i].Span;
                for (int p = 0; p < proofs.Length; p++)
                {
                    if (p > 0) writer.Write(","u8);
                    writer.Write("\"0x"u8);
                    HexWriter.WriteHexSmall(writer, proofs[p]);
                    writer.Write("\""u8);
                }

                writer.Write("]}"u8);
            }

            // Flush after each entry for backpressure
            FlushResult flushResult = await writer.FlushAsync(cancellationToken);
            if (flushResult.IsCompleted || flushResult.IsCanceled)
                return;
        }

        writer.Write("]"u8);
    }

    // Used only by tests and STJ fallback; production path is WriteToAsync
    public IEnumerator<BlobAndProofV2?> GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
        {
            byte[]? blob = _blobs[i];
            if (blob is null)
            {
                yield return null;
            }
            else
            {
                yield return new BlobAndProofV2(blob, _proofs[i].ToArray());
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
