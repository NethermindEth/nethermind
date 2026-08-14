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
    private readonly ArrayPoolList<byte[]?> _blobs;
    private readonly ArrayPoolList<ReadOnlyMemory<byte[]>> _proofs;
    private readonly BlobCellsAndProofs?[] _response;
    private readonly int _count;

    public BlobsV4DirectResponse(
        ArrayPoolList<byte[]?> blobs,
        ArrayPoolList<ReadOnlyMemory<byte[]>> proofs,
        BlobCellsAndProofs?[] response,
        int count)
    {
        Debug.Assert(count <= blobs.Count && count <= proofs.Count && count <= response.Length,
            "count must not exceed array lengths");
        _blobs = blobs;
        _proofs = proofs;
        _response = response;
        _count = count;
    }

    public int Count => _count;

    public BlobCellsAndProofs? this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)_count, nameof(index));
            return _response[index];
        }
    }

    public ValueTask WriteToAsync(PipeWriter writer, CancellationToken cancellationToken) =>
        StreamableResultWriter.WriteArrayAsync(writer, _count, new ItemWriter(_response), cancellationToken);

    IEnumerator<BlobCellsAndProofs?> IEnumerable<BlobCellsAndProofs?>.GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
        {
            yield return _response[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<BlobCellsAndProofs?>)this).GetEnumerator();

    public void Dispose()
    {
        _blobs.Dispose();
        _proofs.Dispose();
        if (_response is not null)
        {
            for (int i = 0; i < _count; i++)
            {
                BlobCellsAndProofs? item = _response[i];
                if (item is not null && item.Available)
                {
                    if (item.BlobCells is not null)
                    {
                        for (int j = 0; j < Ckzg.CellsPerExtBlob; j++)
                        {
                            byte[]? cell = item.BlobCells[j];
                            if (cell is not null)
                            {
                                ArrayPool<byte>.Shared.Return(cell);
                            }
                        }
                        ArrayPool<byte[]?>.Shared.Return(item.BlobCells, clearArray: true);
                    }
                    if (item.Proofs is not null)
                    {
                        for (int j = 0; j < Ckzg.CellsPerExtBlob; j++)
                        {
                            byte[]? proof = item.Proofs[j];
                            if (proof is not null)
                            {
                                ArrayPool<byte>.Shared.Return(proof);
                            }
                        }
                        ArrayPool<byte[]?>.Shared.Return(item.Proofs, clearArray: true);
                    }
                }
            }
            ArrayPool<BlobCellsAndProofs?>.Shared.Return(_response, clearArray: true);
        }
    }

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

            writer.Write("{\"available\":true,\"blobCells\":["u8);

            byte[]?[]? blobCells = item.BlobCells;
            if (blobCells is not null)
            {
                for (int c = 0; c < Ckzg.CellsPerExtBlob; c++)
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
                for (int p = 0; p < Ckzg.CellsPerExtBlob; p++)
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
