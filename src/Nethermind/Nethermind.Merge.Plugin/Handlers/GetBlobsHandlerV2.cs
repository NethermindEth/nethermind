// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
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
            var error = $"The number of requested blobs must not exceed {MaxRequest}";
            return ResultWrapper<IEnumerable<BlobAndProofV2?>?>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        Metrics.GetBlobsRequestsTotal += request.BlobVersionedHashes.Length;

        int count = txPool.GetBlobCounts(request.BlobVersionedHashes);
        Metrics.GetBlobsRequestsInBlobpoolTotal += count;

        // quick fail if we don't have some blob (unless partial return is allowed)
        if (!request.AllowPartialReturn && count != request.BlobVersionedHashes.Length)
        {
            return ReturnEmptyArray();
        }

        ArrayPoolList<BlobAndProofV2?> response = new(request.BlobVersionedHashes.Length);

        try
        {
            foreach (byte[] requestedBlobVersionedHash in request.BlobVersionedHashes)
            {
                if (txPool.TryGetBlobAndProofV1(requestedBlobVersionedHash, out byte[]? blob, out byte[][]? cellProofs))
                {
                    response.Add(new BlobAndProofV2(blob, cellProofs));
                }
                else if (request.AllowPartialReturn)
                {
                    response.Add(null);
                }
                else
                {
                    // fail if we were not able to collect full blob data
                    response.Dispose();
                    return ReturnEmptyArray();
                }
            }

            Metrics.GetBlobsRequestsSuccessTotal++;
            return ResultWrapper<IEnumerable<BlobAndProofV2?>?>.Success(response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private Task<ResultWrapper<IEnumerable<BlobAndProofV2>?>> ReturnEmptyArray()
    {
        Metrics.GetBlobsRequestsFailureTotal++;
        return NotFound;
    }
}

public readonly record struct GetBlobsHandlerV2Request(byte[][] BlobVersionedHashes, bool AllowPartialReturn = false);
