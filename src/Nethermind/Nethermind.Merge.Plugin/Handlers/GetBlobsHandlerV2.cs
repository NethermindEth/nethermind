// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.JsonRpc;
using Nethermind.TxPool;
using Nethermind.TxPool.Collections;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetBlobsHandlerV2(ITxPool txPool) : IAsyncHandler<GetBlobsHandlerV2Request, IEnumerable<BlobAndProofV2?>?>
{
    private const int MaxRequest = 128;
    private static string _overMaxRequestError = $"The number of requested blobs must not exceed {MaxRequest}";

    private static readonly Task<ResultWrapper<IEnumerable<BlobAndProofV2?>?>> NotFound = Task.FromResult(ResultWrapper<IEnumerable<BlobAndProofV2?>?>.Success(null));

    public Task<ResultWrapper<IEnumerable<BlobAndProofV2?>?>> HandleAsync(GetBlobsHandlerV2Request request)
    {
        int length = request.BlobVersionedHashes.Length;

        if (length > MaxRequest)
        {
            return ResultWrapper<IEnumerable<BlobAndProofV2?>?>.Fail(_overMaxRequestError, MergeErrorCodes.TooLargeRequest);
        }

        Metrics.GetBlobsRequestsTotal += length;

        ArrayPoolList<BlobAndProofV2?> response = new(length);
        txPool.TryGetBlobsAndProofsV2(request.BlobVersionedHashes, response);

        for (int i = 0; i < response.Count; i++)
        {
            if (response[i] is not null)
            {
                Metrics.GetBlobsRequestsInBlobpoolTotal++;
            }
            else if (!request.AllowPartialReturn)
            {
                response.Dispose();
                return ReturnEmptyArray();
            }
        }

        Metrics.GetBlobsRequestsSuccessTotal++;
        return ResultWrapper<IEnumerable<BlobAndProofV2?>?>.Success(response);
    }

    private Task<ResultWrapper<IEnumerable<BlobAndProofV2?>?>> ReturnEmptyArray()
    {
        Metrics.GetBlobsRequestsFailureTotal++;
        return NotFound;
    }
}

public readonly record struct GetBlobsHandlerV2Request(byte[][] BlobVersionedHashes, bool AllowPartialReturn = false);
