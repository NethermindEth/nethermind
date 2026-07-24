// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Facade.Eth;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>Handles Amsterdam blob-cell retrieval requests.</summary>
public class GetBlobsHandlerV4(ITxPool txPool, IEthSyncingInfo? ethSyncingInfo) : IAsyncHandler<GetBlobsHandlerV4Request, IReadOnlyList<BlobCellsAndProofs?>?>
{
    private static readonly Task<ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>> NotAvailable = Task.FromResult(ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>.Success(null));

    public GetBlobsHandlerV4(ITxPool txPool)
        : this(txPool, null)
    {
    }

    /// <inheritdoc/>
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

        if (request.BlobVersionedHashes.Length > GetBlobsV4Limits.MaxBlobVersionedHashes)
        {
            string tooLarge = $"The number of requested blobs must not exceed {GetBlobsV4Limits.MaxBlobVersionedHashes}";
            return ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>.Fail(tooLarge, MergeErrorCodes.TooLargeRequest);
        }

        for (int i = 0; i < request.BlobVersionedHashes.Length; i++)
        {
            if (request.BlobVersionedHashes[i] is not { Length: Eip4844Constants.BytesPerBlobVersionedHash })
            {
                return ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>.Fail(
                    $"Blob versioned hash at index {i} must be exactly {Eip4844Constants.BytesPerBlobVersionedHash} bytes.",
                    ErrorCodes.InvalidParams);
            }
        }

        Metrics.GetBlobsRequestsTotal += request.BlobVersionedHashes.Length;
        if (ethSyncingInfo?.IsSyncing() is true)
        {
            Metrics.GetBlobsRequestsFailureTotal++;
            return NotAvailable;
        }

        int n = request.BlobVersionedHashes.Length;
        int found = 0;
        bool allRequestedCellsAvailable = true;
        BlobCellsAndProofs?[]? response = null;
        try
        {
            response = ArrayPool<BlobCellsAndProofs?>.Shared.Rent(n);
            Array.Clear(response, 0, n);

            for (int i = 0; i < n; i++)
            {
                byte[] blobVersionedHash = request.BlobVersionedHashes[i];
                if (!txPool.TryGetBlobCellsAndProofsV1(blobVersionedHash, requestedMask, out BlobCellMask availableMask, out byte[][]? availableCells, out byte[][]? availableProofs))
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

            return ResultWrapper<IReadOnlyList<BlobCellsAndProofs?>?>.Success(new BlobsV4DirectResponse(response, n));
        }
        catch
        {
            if (response is not null)
            {
                ReturnResponse(response);
            }

            throw;
        }
    }

    private static BlobCellsAndProofs CreateResponseEntry(BlobCellMask requestedMask, BlobCellMask availableMask, byte[][] availableCells, byte[][] availableProofs)
    {
        byte[]?[] blobCells = new byte[]?[requestedMask.Count];
        byte[]?[] cellProofs = new byte[]?[requestedMask.Count];
        if (availableCells.Length != availableMask.Count || availableProofs.Length != availableMask.Count)
        {
            throw new InvalidOperationException("Blob pool returned an inconsistent cell response.");
        }

        int availableIndex = 0;
        int responseIndex = 0;
        foreach (int cellIndex in requestedMask.EnumerateSetBits())
        {
            if (availableMask.Contains(cellIndex))
            {
                byte[] cell = availableCells[availableIndex];
                byte[] proof = availableProofs[availableIndex];
                if (cell.Length != Ckzg.BytesPerCell || proof.Length != Ckzg.BytesPerProof)
                {
                    throw new InvalidOperationException("Blob pool returned a malformed cell or proof.");
                }

                blobCells[responseIndex] = cell;
                cellProofs[responseIndex] = proof;
                availableIndex++;
            }

            responseIndex++;
        }

        return new BlobCellsAndProofs
        {
            Available = true,
            BlobCells = blobCells,
            Proofs = cellProofs,
            RequestedMask = requestedMask
        };
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

    private static void ReturnResponse(BlobCellsAndProofs?[] response) =>
        ArrayPool<BlobCellsAndProofs?>.Shared.Return(response, clearArray: true);
}

/// <summary>Blob hashes and cell indices requested through <c>engine_getBlobsV4</c>.</summary>
public readonly record struct GetBlobsHandlerV4Request(byte[][] BlobVersionedHashes, BitArray IndicesBitarray);
