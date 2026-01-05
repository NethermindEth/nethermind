// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Headers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetBALsByRangeV1Handler(IBlockAccessListStore balStore, IBlockFinder blockFinder) : IAsyncHandler<(long Start, long Count), IEnumerable<byte[]>?>
{
    // private const int MaxRequest = 128;

    // private static readonly Task<ResultWrapper<IEnumerable<BlobAndProofV2>?>> NotFound = Task.FromResult(ResultWrapper<IEnumerable<BlobAndProofV2>?>.Success(null));

    public Task<ResultWrapper<IEnumerable<byte[]>?>> HandleAsync((long Start, long Count) request)
    {
        // if (request.Length > MaxRequest)
        // {
        //     var error = $"The number of requested blobs must not exceed {MaxRequest}";
        //     return ResultWrapper<IEnumerable<BlobAndProofV2>?>.Fail(error, MergeErrorCodes.TooLargeRequest);
        // }

        // impose max?
        ArrayPoolList<byte[]> response = new((int)request.Count);

        int end = (int)(request.Start + request.Count);
        for (int i = (int)request.Start; i < end; i++)
        {
            Hash256? blockHash = blockFinder.FindBlockHash(i);
            byte[]? bal = blockHash is null ? null : balStore.Get(blockHash);

            if (bal is null)
            {
                return ResultWrapper<IEnumerable<byte[]>?>.Success(null);
            }

            response.Add(bal);
        }

        return ResultWrapper<IEnumerable<byte[]>?>.Success(response);
        // try
        // {
        //     foreach (byte[] requestedBlobVersionedHash in request)
        //     {
        //         if (txPool.TryGetBlobAndProofV1(requestedBlobVersionedHash, out byte[]? blob, out byte[][]? cellProofs))
        //         {
        //             response.Add(new BlobAndProofV2(blob, cellProofs));
        //         }
        //         else
        //         {
        //             // fail if we were not able to collect full blob data
        //             response.Dispose();
        //             return ReturnEmptyArray();
        //         }
        //     }

        //     Metrics.GetBlobsRequestsSuccessTotal++;
        //     return ResultWrapper<IEnumerable<BlobAndProofV2>?>.Success(response);
        // }
        // catch
        // {
        //     response.Dispose();
        //     throw;
        // }
    }

    // private Task<ResultWrapper<IEnumerable<BlobAndProofV2>?>> ReturnEmptyArray()
    // {
    //     Metrics.GetBlobsRequestsFailureTotal++;
    //     return NotFound;
    // }
}
