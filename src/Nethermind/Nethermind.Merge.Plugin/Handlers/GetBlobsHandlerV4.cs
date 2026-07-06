// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetBlobsHandlerV4(ITxPool txPool, IEthSyncingInfo ethSyncingInfo) : IAsyncHandler<GetBlobsHandlerV4Request, IReadOnlyList<BlobCellsAndProofs?>?>
{
    private const int MaxRequest = 128;

    private static readonly Task<ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>> NotAvailable = Task.FromResult(ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>.Success(null));

    public Task<ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>> HandleAsync(GetBlobsHandlerV4Request request)
    {
        if (request.BlobVersionedHashes is null)
        {
            return ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>.Fail("Blob versioned hashes are required.", ErrorCodes.InvalidParams);
        }

        if (!TryGetBlobCellMask(request.IndicesBitarray, out BlobCellMask requestedMask, out string? error))
        {
            return ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>.Fail(error!, ErrorCodes.InvalidParams);
        }

        if (request.BlobVersionedHashes.Length > MaxRequest)
        {
            string tooLarge = $"The number of requested blobs must not exceed {MaxRequest}";
            return ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>.Fail(tooLarge, MergeErrorCodes.TooLargeRequest);
        }

        Metrics.GetBlobsRequestsTotal += request.BlobVersionedHashes.Length;
        if (ethSyncingInfo.IsSyncing())
        {
            Metrics.GetBlobsRequestsFailureTotal++;
            return NotAvailable;
        }

        int n = request.BlobVersionedHashes.Length;
        int found = 0;
        bool allRequestedCellsAvailable = true;
        ArrayPoolList<byte[]?> blobs = new(n, n);
        ArrayPoolList<ReadOnlyMemory<byte[]>> proofs = new(n, n);
        BlobCellsAndProofs?[]? response = null;
        try
        {
            response = ArrayPool<BlobCellsAndProofs?>.Shared.Rent(n);
            Array.Clear(response, 0, n);

            for (int i = 0; i < n; i++)
            {
                byte[] blobVersionedHash = request.BlobVersionedHashes[i];
                if (blobVersionedHash is not { Length: Eip4844Constants.BytesPerBlobVersionedHash }
                    || !txPool.TryGetBlobCellsAndProofsV1(blobVersionedHash, requestedMask, out BlobCellMask availableMask, out byte[][]? availableCells, out byte[][]? availableProofs))
                {
                    allRequestedCellsAvailable = false;
                    continue;
                }

                found++;
                if (availableMask != requestedMask)
                {
                    allRequestedCellsAvailable = false;
                }

                response[i] = CreateResponseEntry(requestedMask, availableMask, availableCells, availableProofs);
            }

            Metrics.GetBlobsRequestsInBlobpoolTotal += found;
            if (allRequestedCellsAvailable)
            {
                Metrics.GetBlobsRequestsSuccessTotal++;
            }
            else
            {
                Metrics.GetBlobsRequestsFailureTotal++;
            }

            return ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>.Success(new BlobsV4DirectResponse(blobs, proofs, response, n));
        }
        catch
        {
            blobs.Dispose();
            proofs.Dispose();
            if (response is not null)
            {
                ReturnResponse(response, n);
            }

            throw;
        }
    }

    private static BlobCellsAndProofs CreateResponseEntry(BlobCellMask requestedMask, BlobCellMask availableMask, byte[][] availableCells, byte[][] availableProofs)
    {
        byte[]?[] blobCells = ArrayPool<byte[]?>.Shared.Rent(Ckzg.CellsPerExtBlob);
        byte[]?[] cellProofs = ArrayPool<byte[]?>.Shared.Rent(Ckzg.CellsPerExtBlob);
        Array.Clear(blobCells, 0, Ckzg.CellsPerExtBlob);
        Array.Clear(cellProofs, 0, Ckzg.CellsPerExtBlob);

        try
        {
            int availableIndex = 0;
            foreach (int cellIndex in requestedMask.EnumerateSetBits())
            {
                if (!availableMask.Contains(cellIndex))
                {
                    continue;
                }

                byte[] cell = ArrayPool<byte>.Shared.Rent(Ckzg.BytesPerCell);
                availableCells[availableIndex].AsSpan(0, Ckzg.BytesPerCell).CopyTo(cell);
                blobCells[cellIndex] = cell;

                byte[] proof = ArrayPool<byte>.Shared.Rent(Ckzg.BytesPerProof);
                availableProofs[availableIndex].AsSpan(0, Ckzg.BytesPerProof).CopyTo(proof);
                cellProofs[cellIndex] = proof;

                availableIndex++;
            }

            return new BlobCellsAndProofs
            {
                Available = true,
                BlobCells = blobCells,
                Proofs = cellProofs
            };
        }
        catch
        {
            ReturnCells(blobCells, cellProofs);
            throw;
        }
    }

    private static bool TryGetBlobCellMask(BitArray? bitarray, out BlobCellMask cellMask, out string? error)
    {
        cellMask = default;
        if (bitarray is null || bitarray.Length != BlobCellMask.CellCount)
        {
            error = $"Blob cell bitarray must be exactly {BlobCellMask.CellCount} bits.";
            return false;
        }

        Span<byte> bytes = stackalloc byte[BlobCellMask.FixedByteLength];
        for (int i = 0; i < BlobCellMask.CellCount; i++)
        {
            if (bitarray.Get(i))
            {
                bytes[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        cellMask = BlobCellMask.FromBytes(bytes);
        error = null;
        return true;
    }

    private static void ReturnResponse(BlobCellsAndProofs?[] response, int count)
    {
        for (int i = 0; i < count; i++)
        {
            BlobCellsAndProofs? item = response[i];
            if (item is not null && item.Available && item.BlobCells is not null && item.Proofs is not null)
            {
                ReturnCells(item.BlobCells, item.Proofs);
            }
        }

        ArrayPool<BlobCellsAndProofs?>.Shared.Return(response, clearArray: true);
    }

    private static void ReturnCells(byte[]?[] blobCells, byte[]?[] cellProofs)
    {
        for (int cellIdx = 0; cellIdx < Ckzg.CellsPerExtBlob; cellIdx++)
        {
            if (blobCells[cellIdx] is { } cell)
            {
                ArrayPool<byte>.Shared.Return(cell);
            }

            if (cellProofs[cellIdx] is { } proof)
            {
                ArrayPool<byte>.Shared.Return(proof);
            }
        }

        ArrayPool<byte[]?>.Shared.Return(blobCells, clearArray: true);
        ArrayPool<byte[]?>.Shared.Return(cellProofs, clearArray: true);
    }
}

public readonly record struct GetBlobsHandlerV4Request(byte[][] BlobVersionedHashes, BitArray IndicesBitarray);
