// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.JsonRpc;
using Nethermind.TxPool;
using Nethermind.TxPool.Collections;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetBlobsHandlerV1(ITxPool txPool, IChainHeadSpecProvider chainHeadSpecProvider) : IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>>
{
    private const int MaxRequest = 128;
    private static string _overMaxRequestError = $"The number of requested blobs must not exceed {MaxRequest}";

    public Task<ResultWrapper<IEnumerable<BlobAndProofV1?>>> HandleAsync(byte[][] request)
    {
        if (chainHeadSpecProvider.GetCurrentHeadSpec().IsEip7594Enabled)
        {
            return ResultWrapper<IEnumerable<BlobAndProofV1?>>.Fail(MergeErrorMessages.UnsupportedFork, MergeErrorCodes.UnsupportedFork);
        }

        int length = request.Length;
        if (length > MaxRequest)
        {
            return ResultWrapper<IEnumerable<BlobAndProofV1?>>.Fail(_overMaxRequestError, MergeErrorCodes.TooLargeRequest);
        }

        Metrics.NumberOfRequestedBlobs += length;

        ArrayPoolList<BlobAndProofV1?> response = new(length);
        txPool.TryGetBlobsAndProofsV1(request, response);

        bool allBlobsAvailable = true;
        for (int i = 0; i < response.Count; i++)
        {
            if (response[i] is not null)
            {
                Metrics.NumberOfSentBlobs++;
            }
            else
            {
                allBlobsAvailable = false;
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

        return ResultWrapper<IEnumerable<BlobAndProofV1?>>.Success(response);
    }
}
