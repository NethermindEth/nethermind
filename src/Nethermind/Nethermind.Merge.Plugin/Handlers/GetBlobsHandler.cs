// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetBlobsHandler(ITxPool txPool, IChainHeadSpecProvider chainHeadSpecProvider) : IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>>
{
    private const int MaxRequest = 128;

    public Task<ResultWrapper<IEnumerable<BlobAndProofV1?>>> HandleAsync(byte[][] request)
    {
        if (chainHeadSpecProvider.GetCurrentHeadSpec().IsEip7594Enabled)
        {
            return ResultWrapper<IEnumerable<BlobAndProofV1?>>.Fail(MergeErrorMessages.UnsupportedFork, MergeErrorCodes.UnsupportedFork);
        }

        if (request.Length > MaxRequest)
        {
            var error = $"The number of requested blobs must not exceed {MaxRequest}";
            return ResultWrapper<IEnumerable<BlobAndProofV1?>>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        bool allBlobsAvailable = true;
        Metrics.NumberOfRequestedBlobs += request.Length;

        ArrayPoolList<BlobAndProofV1?> response = new(request.Length);
        try
        {
            foreach (byte[] requestedBlobVersionedHash in request)
            {
                if (txPool.TryGetBlobAndProofV0(requestedBlobVersionedHash, out byte[]? blob, out byte[]? proof))
                {
                    Metrics.NumberOfSentBlobs++;
                    response.Add(new BlobAndProofV1(blob, proof));
                }
                else
                {
                    allBlobsAvailable = false;
                    response.Add(null);
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

            return ResultWrapper<IEnumerable<BlobAndProofV1?>>.Success(new BlobsV1DirectResponse(response));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
