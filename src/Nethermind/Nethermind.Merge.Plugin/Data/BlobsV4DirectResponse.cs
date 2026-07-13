// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using CkzgLib;
using Nethermind.Core.Collections;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Json;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>Writes blob-cells-and-proofs V4 results directly into a <see cref="PipeWriter"/>.</summary>
public sealed class BlobsV4DirectResponse : IStreamableResult, IReadOnlyList<BlobCellsAndProofs?>, IDisposable
{
    private BlobCellsAndProofs?[]? _response;
    private readonly int _count;
    private readonly ArrayPoolList<byte[]?>? _legacyBlobs;
    private readonly ArrayPoolList<ReadOnlyMemory<byte[]>>? _legacyProofs;

    /// <summary>Creates a streamed response over the first <paramref name="count"/> entries.</summary>
    /// <param name="response">Pool-rented response storage owned by this instance.</param>
    /// <param name="count">Number of initialized entries.</param>
    public BlobsV4DirectResponse(
        BlobCellsAndProofs?[] response,
        int count)
    {
        Debug.Assert(count <= response.Length, "count must not exceed array length");
        _response = response;
        _count = count;
    }

    public BlobsV4DirectResponse(
        ArrayPoolList<byte[]?> blobs,
        ArrayPoolList<ReadOnlyMemory<byte[]>> proofs,
        BlobCellsAndProofs?[] response,
        int count)
        : this(response, count)
    {
        _legacyBlobs = blobs;
        _legacyProofs = proofs;
    }

    /// <inheritdoc/>
    public int Count => _count;

    /// <inheritdoc/>
    public BlobCellsAndProofs? this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count, nameof(index));
            return GetResponse()[index];
        }
    }

    /// <inheritdoc/>
    public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken) =>
        StreamableResultWriter.WriteArrayAsync(writer, _count, new ItemWriter(GetResponse()), cancellationToken);

    IEnumerator<BlobCellsAndProofs?> IEnumerable<BlobCellsAndProofs?>.GetEnumerator()
    {
        BlobCellsAndProofs?[] response = GetResponse();
        for (int i = 0; i < _count; i++)
        {
            yield return response[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<BlobCellsAndProofs?>)this).GetEnumerator();

    /// <inheritdoc/>
    public void Dispose()
    {
        BlobCellsAndProofs?[]? response = Interlocked.Exchange(ref _response, null);
        if (response is not null)
        {
            _legacyBlobs?.Dispose();
            _legacyProofs?.Dispose();
            if (_legacyBlobs is not null || _legacyProofs is not null)
            {
                ReturnLegacyCellBuffers(response);
            }

            ArrayPool<BlobCellsAndProofs?>.Shared.Return(response, clearArray: true);
        }
    }

    private void ReturnLegacyCellBuffers(BlobCellsAndProofs?[] response)
    {
        for (int i = 0; i < _count; i++)
        {
            BlobCellsAndProofs? item = response[i];
            if (item is null || !item.Available)
            {
                continue;
            }

            ReturnLegacyBuffers(item.BlobCells);
            ReturnLegacyBuffers(item.Proofs);
        }

        static void ReturnLegacyBuffers(byte[]?[]? buffers)
        {
            if (buffers is null)
            {
                return;
            }

            for (int i = 0; i < buffers.Length; i++)
            {
                if (buffers[i] is { } buffer)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }

            ArrayPool<byte[]?>.Shared.Return(buffers, clearArray: true);
        }
    }

    private BlobCellsAndProofs?[] GetResponse() =>
        _response ?? throw new ObjectDisposedException(nameof(BlobsV4DirectResponse));

    private readonly struct ItemWriter(BlobCellsAndProofs?[] response) : IJsonArrayItemWriter
    {
        public void WriteItem(PipeWriter writer, int index)
        {
            BlobCellsAndProofs? item = response[index];
            if (item is null || !item.Available)
            {
                writer.Write("null"u8);
                return;
            }

            writer.Write("{\"blob_cells\":["u8);

            byte[]?[]? blobCells = item.BlobCells;
            if (blobCells is not null)
            {
                for (int c = 0; c < blobCells.Length; c++)
                {
                    if (c > 0) writer.Write(","u8);
                    byte[]? cell = blobCells[c];
                    if (cell is null)
                    {
                        writer.Write("null"u8);
                    }
                    else
                    {
                        HexWriter.WriteHexString(writer, cell.AsSpan(0, Ckzg.BytesPerCell), chunked: true);
                    }
                }
            }

            writer.Write("],\"proofs\":["u8);

            byte[]?[]? proofs = item.Proofs;
            if (proofs is not null)
            {
                for (int p = 0; p < proofs.Length; p++)
                {
                    if (p > 0) writer.Write(","u8);
                    byte[]? proof = proofs[p];
                    if (proof is null)
                    {
                        writer.Write("null"u8);
                    }
                    else
                    {
                        HexWriter.WriteHexString(writer, proof.AsSpan(0, Ckzg.BytesPerProof), chunked: false);
                    }
                }
            }

            writer.Write("]}"u8);
        }
    }
}
