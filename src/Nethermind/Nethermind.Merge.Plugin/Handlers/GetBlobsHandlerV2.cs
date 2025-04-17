// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetBlobsHandlerV2(ITxPool txPool) : IAsyncHandler<List<BlobVersionedHash>, List<BlobAndProofV2>>
{
    private const int MaxRequest = 128;

    public Task<ResultWrapper<List<BlobAndProofV2>>> HandleAsync(List<BlobVersionedHash> request)
    {
        if (request.Count > MaxRequest)
        {
            var error = $"The number of requested blobs must not exceed {MaxRequest}";
            return ResultWrapper<List<BlobAndProofV2>>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        Metrics.NumberOfRequestedBlobs += request.Count;

        // quick fail if we don't have some blob
        if (!txPool.AreBlobsAvailable(request.Select(x => (byte[])x).ToArray()))
        {
            return ReturnEmptyArray();
        }

        using ArrayPoolList<BlobAndProofV2> response = new(request.Count);
        foreach (byte[] requestedBlobVersionedHash in request)
        {
            if (txPool.TryGetBlobAndProofV2(requestedBlobVersionedHash, out byte[]? blob, out byte[][]? cellProofs))
            {
                response.Add(new BlobAndProofV2(blob, cellProofs.Select(p => (Proof)p).ToArray()));
            }
            else
            {
                // fail if we were not able to collect full blob data
                return ReturnEmptyArray();
            }
        }

        Metrics.NumberOfSentBlobs += request.Count;
        Metrics.NumberOfGetBlobsSuccesses++;
        // (List<BlobVersionedHash>)r)
        return ResultWrapper<List<BlobAndProofV2>>.Success(response.ToList(), (r) => SszEncoding.Encode((List<BlobAndProofV2>)r));
    }

    private ResultWrapper<List<BlobAndProofV2>> ReturnEmptyArray()
    {
        Metrics.NumberOfGetBlobsFailures++;
        return ResultWrapper<List<BlobAndProofV2>>.Success([]);
    }
}
