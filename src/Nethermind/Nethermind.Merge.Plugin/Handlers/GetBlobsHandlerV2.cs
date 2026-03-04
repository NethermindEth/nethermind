// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetBlobsHandlerV2(ITxPool txPool) : IAsyncHandler<GetBlobsHandlerV2Request, IEnumerable<BlobAndProofV2?>?>
{
    private const int MaxRequest = 128;

    private static readonly Task<ResultWrapper<IEnumerable<BlobAndProofV2?>?>> NotFound = Task.FromResult(ResultWrapper<IEnumerable<BlobAndProofV2?>?>.Success(null));

    public Task<ResultWrapper<IEnumerable<BlobAndProofV2?>?>> HandleAsync(GetBlobsHandlerV2Request request)
    {
        if (request.BlobVersionedHashes.Length > MaxRequest)
        {
            string error = $"The number of requested blobs must not exceed {MaxRequest}";
            return ResultWrapper<IEnumerable<BlobAndProofV2?>?>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        Metrics.GetBlobsRequestsTotal += request.BlobVersionedHashes.Length;

        int n = request.BlobVersionedHashes.Length;
        byte[]?[] blobs = new byte[n][];
        ReadOnlyMemory<byte[]>[] proofs = new ReadOnlyMemory<byte[]>[n];
        int count = txPool.TryGetBlobsAndProofsV1(request.BlobVersionedHashes, blobs, proofs);

        Metrics.GetBlobsRequestsInBlobpoolTotal += count;

        // quick fail if we don't have some blob (unless partial return is allowed)
        if (!request.AllowPartialReturn && count != n)
        {
            return ReturnEmptyArray();
        }

        Metrics.GetBlobsRequestsSuccessTotal++;
        return ResultWrapper<IEnumerable<BlobAndProofV2?>?>.Success(new BlobsV2DirectResponse(blobs, proofs, n));
    }

    private Task<ResultWrapper<IEnumerable<BlobAndProofV2?>?>> ReturnEmptyArray()
    {
        Metrics.GetBlobsRequestsFailureTotal++;
        return NotFound;
    }
}

public readonly record struct GetBlobsHandlerV2Request(byte[][] BlobVersionedHashes, bool AllowPartialReturn = false);
