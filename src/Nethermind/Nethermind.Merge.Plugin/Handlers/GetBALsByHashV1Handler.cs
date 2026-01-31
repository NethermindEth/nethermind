// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain.Headers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetBALsByHashV1Handler(IBlockAccessListStore balStore) : IAsyncHandler<Hash256[], IEnumerable<byte[]?>>
{
    // private const int MaxRequest = 128;

    // private static readonly Task<ResultWrapper<IEnumerable<BlobAndProofV2>?>> NotFound = Task.FromResult(ResultWrapper<IEnumerable<BlobAndProofV2>?>.Success(null));

    public Task<ResultWrapper<IEnumerable<byte[]?>>> HandleAsync(Hash256[] request)
    {
        // if (request.Length > MaxRequest)
        // {
        //     var error = $"The number of requested blobs must not exceed {MaxRequest}";
        //     return ResultWrapper<IEnumerable<BlobAndProofV2>?>.Fail(error, MergeErrorCodes.TooLargeRequest);
        // }

        ArrayPoolList<byte[]?> response = new(request.Length);

        foreach (Hash256 blockHash in request)
        {
            byte[]? bal = balStore.GetRlp(blockHash);
            response.Add(bal);
        }

        return ResultWrapper<IEnumerable<byte[]?>>.Success(response);
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
