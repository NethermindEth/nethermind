// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetBlobsHandlerV2(ITxPool txPool) : IAsyncHandler<byte[][], IEnumerable<BlobAndProofV2>>
{
    private const int MaxRequest = 128;


    public Task<ResultWrapper<IEnumerable<BlobAndProofV2>>> HandleAsync(byte[][] request)
    {
        if (request.Length > MaxRequest)
        {
            var error = $"The number of requested blobs must not exceed {MaxRequest}";
            return ResultWrapper<IEnumerable<BlobAndProofV2>>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        Metrics.ExecutionGetBlobsRequestedFromCLTotal += request.Length;

        var count = txPool.GetBlobCounts(request);
        Metrics.ExecutionGetBlobsRequestedFromCLHit += count;

        long startTime = Stopwatch.GetTimestamp();
        try
        {
            // quick fail if we don't have some blob
            if (count != request.Length)
            {
                return ReturnEmptyArray();
            }

            using ArrayPoolList<BlobAndProofV2> response = new(request.Length);
            foreach (byte[] requestedBlobVersionedHash in request)
            {
                if (txPool.TryGetBlobAndProofV2(requestedBlobVersionedHash, out byte[]? blob, out byte[][]? cellProofs))
                {
                    response.Add(new BlobAndProofV2(blob, cellProofs));
                }
                else
                {
                    // fail if we were not able to collect full blob data
                    return ReturnEmptyArray();
                }
            }

            return ResultWrapper<IEnumerable<BlobAndProofV2>>.Success(response.ToList());
        }
        finally
        {
            Metrics.ExecutionGetBlobsRequestDurationSeconds = (long)Stopwatch.GetElapsedTime(startTime).TotalSeconds;
        }
    }

    private ResultWrapper<IEnumerable<BlobAndProofV2>> ReturnEmptyArray()
    {
        Metrics.NumberOfGetBlobsFailures++;
        return ResultWrapper<IEnumerable<BlobAndProofV2>>.Success([]);
    }
}
