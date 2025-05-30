// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetBlobsHandler(ITxPool txPool) : IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>>
{
    private const int MaxRequest = 128;

    public Task<ResultWrapper<IEnumerable<BlobAndProofV1?>>> HandleAsync(byte[][] request)
    {
        if (request.Length > MaxRequest)
        {
            var error = $"The number of requested blobs must not exceed {MaxRequest}";
            return ResultWrapper<IEnumerable<BlobAndProofV1?>>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        return ResultWrapper<IEnumerable<BlobAndProofV1?>>.Success(GetBlobsAndProofs(request));
    }

    private IEnumerable<BlobAndProofV1?> GetBlobsAndProofs(byte[][] request)
    {
        bool allBlobsAvailable = true;
        Metrics.NumberOfRequestedBlobs += request.Length;

        foreach (byte[] requestedBlobVersionedHash in request)
        {
            if (txPool.TryGetBlobAndProofV0(requestedBlobVersionedHash, out byte[]? blob, out byte[]? proof))
            {
                Metrics.NumberOfSentBlobs++;
                yield return new BlobAndProofV1(blob, proof);
            }
            else
            {
                allBlobsAvailable = false;
                yield return null;
            }
        }

        if (allBlobsAvailable)
        {
            Metrics.GetBlobsRequestsSuccessTotal++;
        }
        else
        {
            Metrics.GetBlobsRequestsFailureTotal++;
        }
    }
}
