// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.TxPool;
using System.Threading.Tasks;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetBlobsHandlerV4(ITxPool txPool) : IAsyncHandler<GetBlobsHandlerV4Request, IReadOnlyList<BlobCellsAndProofsV1?>?>
{
    private const int MaxRequest = 128;

    public Task<ResultWrapper<IReadOnlyList<BlobCellsAndProofsV1?>?>> HandleAsync(GetBlobsHandlerV4Request request)
    {
        if (request.BlobVersionedHashes.Length > MaxRequest)
        {
            string error = $"The number of requested blobs must not exceed {MaxRequest}";
            return ResultWrapper<IReadOnlyList<BlobCellsAndProofsV1?>?>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        int requestedCellCount = request.CellMask.Count;
        BlobCellsAndProofsV1?[] result = new BlobCellsAndProofsV1?[request.BlobVersionedHashes.Length];
        bool allBlobsAvailable = true;

        for (int i = 0; i < request.BlobVersionedHashes.Length; i++)
        {
            byte[] blobVersionedHash = request.BlobVersionedHashes[i];
            if (blobVersionedHash is not { Length: Eip4844Constants.BytesPerBlobVersionedHash }
                || !txPool.TryGetBlobCellsAndProofsV1(blobVersionedHash, request.CellMask, out BlobCellMask availableMask, out byte[][]? presentCells, out byte[][]? presentProofs))
            {
                allBlobsAvailable = false;
                continue;
            }

            byte[]?[] cells = new byte[requestedCellCount][];
            byte[]?[] proofs = new byte[requestedCellCount][];
            int requestedIndex = 0;
            int availableIndex = 0;
            foreach (int cellIndex in request.CellMask.EnumerateSetBits())
            {
                if (availableMask.Contains(cellIndex))
                {
                    cells[requestedIndex] = presentCells[availableIndex];
                    proofs[requestedIndex] = presentProofs[availableIndex];
                    availableIndex++;
                }

                requestedIndex++;
            }

            result[i] = new BlobCellsAndProofsV1(cells, proofs);
        }

        Metrics.GetBlobsRequestsTotal += request.BlobVersionedHashes.Length;
        if (allBlobsAvailable)
        {
            Metrics.GetBlobsRequestsSuccessTotal++;
        }
        else
        {
            Metrics.GetBlobsRequestsFailureTotal++;
        }

        return ResultWrapper<IReadOnlyList<BlobCellsAndProofsV1?>?>.Success(result);
    }
}

public readonly record struct GetBlobsHandlerV4Request(byte[][] BlobVersionedHashes, BlobCellMask CellMask);
