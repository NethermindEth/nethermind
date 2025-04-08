// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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

        Metrics.NumberOfRequestedBlobs += request.Length;

        // quick fail if we don't have some blob
        if (!txPool.AreBlobsAvailable(request))
        {
            return ReturnEmptyArray();
        }

        ArrayPoolList<BlobAndProofV2> response = new ArrayPoolList<BlobAndProofV2>(request.Length);
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

        Metrics.NumberOfSentBlobs += request.Length;
        Metrics.NumberOfGetBlobsSuccesses++;
        return ResultWrapper<IEnumerable<BlobAndProofV2>>.Success(response);
    }

    private ResultWrapper<IEnumerable<BlobAndProofV2>> ReturnEmptyArray()
    {
        Metrics.NumberOfGetBlobsFailures++;
        return ResultWrapper<IEnumerable<BlobAndProofV2>>.Success([]);
    }
}
