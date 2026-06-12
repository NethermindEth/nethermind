// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using CkzgLib;
using Nethermind.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetBlobsHandlerV4(ITxPool txPool) : IAsyncHandler<GetBlobsHandlerV4Request, IReadOnlyList<BlobCellsAndProofs?>?>
{
    private const int MaxRequest = 128;
    private const int CellsBufferSize = Ckzg.CellsPerExtBlob * Ckzg.BytesPerCell;

    public Task<ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>> HandleAsync(GetBlobsHandlerV4Request request)
    {
        if (request.BlobVersionedHashes.Length > MaxRequest)
        {
            string error = $"The number of requested blobs must not exceed {MaxRequest}";
            return ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        Metrics.GetBlobsRequestsTotal += request.BlobVersionedHashes.Length;

        int n = request.BlobVersionedHashes.Length;
        byte[]?[] blobs = new byte[n][];
        ReadOnlyMemory<byte[]>[] proofs = new ReadOnlyMemory<byte[]>[n];
        int count = txPool.TryGetBlobsAndProofsV1(request.BlobVersionedHashes, blobs, proofs);

        Metrics.GetBlobsRequestsInBlobpoolTotal += count;

        BlobCellsAndProofs?[] response = new BlobCellsAndProofs?[n];

        // Reuse one large scratch buffer for ComputeCells across all blobs in this request.
        byte[] cellsBuffer = ArrayPool<byte>.Shared.Rent(CellsBufferSize);
        try
        {
            Span<byte> cellsSpan = cellsBuffer.AsSpan(0, CellsBufferSize);

            for (int i = 0; i < n; i++)
            {
                byte[]? blob = blobs[i];
                if (blob is null)
                {
                    response[i] = null;
                    continue;
                }

                KzgPolynomialCommitments.ComputeCells(blob, cellsSpan);

                byte[]?[] blobCells = new byte[Ckzg.CellsPerExtBlob][];
                byte[]?[] cellProofs = new byte[Ckzg.CellsPerExtBlob][];
                ReadOnlySpan<byte[]> blobProofs = proofs[i].Span;

                for (int cellIdx = 0; cellIdx < Ckzg.CellsPerExtBlob; cellIdx++)
                {
                    if (!request.IndicesBitarray.Get(cellIdx)) continue;

                    byte[] cell = new byte[Ckzg.BytesPerCell];
                    cellsSpan.Slice(cellIdx * Ckzg.BytesPerCell, Ckzg.BytesPerCell).CopyTo(cell);
                    blobCells[cellIdx] = cell;

                    byte[] cellProof = new byte[Ckzg.BytesPerProof];
                    blobProofs[cellIdx].AsSpan(0, Ckzg.BytesPerProof).CopyTo(cellProof);
                    cellProofs[cellIdx] = cellProof;
                }

                response[i] = new BlobCellsAndProofs
                {
                    Available = true,
                    BlobCells = blobCells,
                    Proofs = cellProofs
                };
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(cellsBuffer);
        }

        Metrics.GetBlobsRequestsSuccessTotal++;
        return ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>.Success(response);
    }
}

public readonly record struct GetBlobsHandlerV4Request(byte[][] BlobVersionedHashes, BitArray IndicesBitarray);
