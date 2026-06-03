// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

    private static readonly Task<ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>> NotFound =
        Task.FromResult(ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>.Success(null));

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

        for (int i = 0; i < n; i++)
        {
            byte[]? blob = blobs[i];
            if (blob is null)
            {
                response[i] = null;
                continue;
            }

            // We have the blob and proofs for this blob.
            // Let's compute all 128 cells.
            byte[] cellsBuffer = new byte[Ckzg.CellsPerExtBlob * Ckzg.BytesPerCell];
            KzgPolynomialCommitments.ComputeCells(blob, cellsBuffer);

            byte[]?[] blobCells = new byte[Ckzg.CellsPerExtBlob][];
            byte[]?[] cellProofs = new byte[Ckzg.CellsPerExtBlob][];

            ReadOnlySpan<byte[]> blobProofs = proofs[i].Span;

            for (int cellIdx = 0; cellIdx < Ckzg.CellsPerExtBlob; cellIdx++)
            {
                if (request.IndicesBitarray.Get(cellIdx))
                {
                    byte[] cell = new byte[Ckzg.BytesPerCell];
                    Buffer.BlockCopy(cellsBuffer, cellIdx * Ckzg.BytesPerCell, cell, 0, Ckzg.BytesPerCell);
                    blobCells[cellIdx] = cell;

                    byte[] cellProof = new byte[Ckzg.BytesPerProof];
                    Buffer.BlockCopy(blobProofs[cellIdx], 0, cellProof, 0, Ckzg.BytesPerProof);
                    cellProofs[cellIdx] = cellProof;
                }
            }

            response[i] = new BlobCellsAndProofs
            {
                Available = true,
                BlobCells = blobCells,
                Proofs = cellProofs
            };
        }

        Metrics.GetBlobsRequestsSuccessTotal++;
        return ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>.Success(response);
    }
}

public readonly record struct GetBlobsHandlerV4Request(byte[][] BlobVersionedHashes, BitArray IndicesBitarray);
