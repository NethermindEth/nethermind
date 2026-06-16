// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetBlobsHandlerV2(ITxPool txPool) : IAsyncHandler<GetBlobsHandlerV2Request, IReadOnlyList<BlobAndProofV2?>?>
{
    private const int MaxRequest = 128;

    private static readonly Task<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>> NotFound = Task.FromResult(ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Success(null));

    public Task<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>> HandleAsync(GetBlobsHandlerV2Request request)
    {
        if (request.BlobVersionedHashes.Length > MaxRequest)
        {
            string error = $"The number of requested blobs must not exceed {MaxRequest}";
            return ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        Metrics.GetBlobsRequestsTotal += request.BlobVersionedHashes.Length;

        int n = request.BlobVersionedHashes.Length;
        ArrayPoolList<byte[]?> blobs = new(n, n);
        ArrayPoolList<ReadOnlyMemory<byte[]>> proofs = new(n, n);
        try
        {
            int count = txPool.TryGetBlobsAndProofsV1(request.BlobVersionedHashes, blobs.AsSpan(), proofs.AsSpan());

            Metrics.GetBlobsRequestsInBlobpoolTotal += count;

            // quick fail if we don't have some blob (unless partial return is allowed)
            if (!request.AllowPartialReturn && count != n)
            {
                blobs.Dispose();
                proofs.Dispose();
                return ReturnEmptyArray();
            }

            Metrics.GetBlobsRequestsSuccessTotal++;
            return ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>.Success(new BlobsV2DirectResponse(blobs, proofs, n));
        }
        catch
        {
            blobs.Dispose();
            proofs.Dispose();
            throw;
        }
    }

    private Task<ResultWrapper<IReadOnlyList<BlobAndProofV2?>?>> ReturnEmptyArray()
    {
        Metrics.GetBlobsRequestsFailureTotal++;
        return NotFound;
    }
}

public readonly record struct GetBlobsHandlerV2Request(byte[][] BlobVersionedHashes, bool AllowPartialReturn = false);
