// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetBlobsHandlerV2(ITxPool txPool) : IAsyncHandler<byte[][], IEnumerable<BlobAndProofV2>?>
{
    private const int MaxRequest = 128;

    private static readonly Task<ResultWrapper<IEnumerable<BlobAndProofV2>?>> NotFound = Task.FromResult(ResultWrapper<IEnumerable<BlobAndProofV2>?>.Success(null));

    public Task<ResultWrapper<IEnumerable<BlobAndProofV2>?>> HandleAsync(byte[][] request)
    {
        if (request.Length > MaxRequest)
        {
            var error = $"The number of requested blobs must not exceed {MaxRequest}";
            return ResultWrapper<IEnumerable<BlobAndProofV2>?>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        Metrics.GetBlobsRequestsTotal += request.Length;

        var count = txPool.GetBlobCounts(request);
        Metrics.GetBlobsRequestsInBlobpoolTotal += count;

        // quick fail if we don't have some blob
        if (count != request.Length)
        {
            return ReturnEmptyArray();
        }

        ArrayPoolList<BlobAndProofV2> response = new(request.Length);

        try
        {
            foreach (byte[] requestedBlobVersionedHash in request)
            {
                if (txPool.TryGetBlobAndProofV1(requestedBlobVersionedHash, out byte[]? blob, out byte[][]? cellProofs))
                {
                    response.Add(new BlobAndProofV2(blob, cellProofs));
                }
                else
                {
                    // fail if we were not able to collect full blob data
                    response.Dispose();
                    return ReturnEmptyArray();
                }
            }

            Metrics.GetBlobsRequestsSuccessTotal++;
            return ResultWrapper<IEnumerable<BlobAndProofV2>?>.Success(response.ToList());
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
