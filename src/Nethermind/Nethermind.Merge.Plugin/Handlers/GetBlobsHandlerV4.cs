// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using CkzgLib;
using Nethermind.Core.Collections;
using Nethermind.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetBlobsHandlerV4(ITxPool txPool) : IAsyncHandler<GetBlobsHandlerV4Request, IReadOnlyList<BlobCellsAndProofs?>?>
{
    private const int MaxRequest = 128;

    public Task<ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>> HandleAsync(GetBlobsHandlerV4Request request)
    {
        if (request.BlobVersionedHashes.Length > MaxRequest)
        {
            string error = $"The number of requested blobs must not exceed {MaxRequest}";
            return ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        Metrics.GetBlobsRequestsTotal += request.BlobVersionedHashes.Length;

        int n = request.BlobVersionedHashes.Length;
        ArrayPoolList<byte[]?> blobs = new(n, n);
        ArrayPoolList<ReadOnlyMemory<byte[]>> proofs = new(n, n);
        BlobCellsAndProofs?[]? response = null;
        try
        {
            int count = txPool.TryGetBlobsAndProofsV1(request.BlobVersionedHashes, blobs.AsSpan(), proofs.AsSpan());

            Metrics.GetBlobsRequestsInBlobpoolTotal += count;

            response = ArrayPool<BlobCellsAndProofs?>.Shared.Rent(n);
            // ArrayPool.Rent returns arrays with stale references. The outer catch and
            // BlobsV4DirectResponse.Dispose iterate response[0..n-1] and would otherwise
            // return arrays from a prior caller to our pool, corrupting it.
            Array.Clear(response, 0, n);

            for (int i = 0; i < n; i++)
            {
                byte[]? blob = blobs[i];
                if (blob is null)
                {
                    response[i] = null;
                    continue;
                }

                using ArrayPoolSpan<byte> cellsBuffer = new(Ckzg.CellsPerExtBlob * Ckzg.BytesPerCell);
                KzgPolynomialCommitments.ComputeCells(blob, cellsBuffer);

                byte[]?[] blobCells = ArrayPool<byte[]?>.Shared.Rent(Ckzg.CellsPerExtBlob);
                byte[]?[] cellProofs = ArrayPool<byte[]?>.Shared.Rent(Ckzg.CellsPerExtBlob);
                // Same rationale as for `response`: unfilled indices (where the bitarray bit is 0)
                // would otherwise expose stale byte[] references to the cleanup paths.
                Array.Clear(blobCells, 0, Ckzg.CellsPerExtBlob);
                Array.Clear(cellProofs, 0, Ckzg.CellsPerExtBlob);

                ReadOnlySpan<byte[]> blobProofs = proofs[i].Span;

                try
                {
                    for (int cellIdx = 0; cellIdx < Ckzg.CellsPerExtBlob; cellIdx++)
                    {
                        if (request.IndicesBitarray.Get(cellIdx))
                        {
                            byte[] cell = ArrayPool<byte>.Shared.Rent(Ckzg.BytesPerCell);
                            cellsBuffer.Slice(cellIdx * Ckzg.BytesPerCell, Ckzg.BytesPerCell).CopyTo(cell);
                            blobCells[cellIdx] = cell;

                            byte[] cellProof = ArrayPool<byte>.Shared.Rent(Ckzg.BytesPerProof);
                            blobProofs[cellIdx].AsSpan(0, Ckzg.BytesPerProof).CopyTo(cellProof);
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
                catch
                {
                    for (int cellIdx = 0; cellIdx < Ckzg.CellsPerExtBlob; cellIdx++)
                    {
                        if (blobCells[cellIdx] is { } cell) ArrayPool<byte>.Shared.Return(cell);
                        if (cellProofs[cellIdx] is { } cellProof) ArrayPool<byte>.Shared.Return(cellProof);
                    }
                    ArrayPool<byte[]?>.Shared.Return(blobCells, clearArray: true);
                    ArrayPool<byte[]?>.Shared.Return(cellProofs, clearArray: true);
                    throw;
                }
            }

            if (count == n) Metrics.GetBlobsRequestsSuccessTotal++;
            else Metrics.GetBlobsRequestsFailureTotal++;
            return ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>.Success(
                new BlobsV4DirectResponse(blobs, proofs, response, n));
        }
        catch
        {
            blobs.Dispose();
            proofs.Dispose();
            if (response is not null)
            {
                for (int i = 0; i < n; i++)
                {
                    BlobCellsAndProofs? item = response[i];
                    if (item is not null && item.Available)
                    {
                        if (item.BlobCells is not null)
                        {
                            for (int cellIdx = 0; cellIdx < Ckzg.CellsPerExtBlob; cellIdx++)
                            {
                                if (item.BlobCells[cellIdx] is { } cell) ArrayPool<byte>.Shared.Return(cell);
                            }
                            ArrayPool<byte[]?>.Shared.Return(item.BlobCells, clearArray: true);
                        }
                        if (item.Proofs is not null)
                        {
                            for (int cellIdx = 0; cellIdx < Ckzg.CellsPerExtBlob; cellIdx++)
                            {
                                if (item.Proofs[cellIdx] is { } cellProof) ArrayPool<byte>.Shared.Return(cellProof);
                            }
                            ArrayPool<byte[]?>.Shared.Return(item.Proofs, clearArray: true);
                        }
                    }
                }
                ArrayPool<BlobCellsAndProofs?>.Shared.Return(response, clearArray: true);
            }
            throw;
        }
    }
}

public readonly record struct GetBlobsHandlerV4Request(byte[][] BlobVersionedHashes, BitArray IndicesBitarray);
