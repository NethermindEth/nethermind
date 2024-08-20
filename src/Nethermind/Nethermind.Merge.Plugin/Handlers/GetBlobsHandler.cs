// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetBlobsHandler(ITxPool txPool) : IAsyncHandler<byte[][], GetBlobsV1Result>
{
    private const int MaxRequest = 128;
    public Task<ResultWrapper<GetBlobsV1Result>> HandleAsync(byte[][] request)
    {
        if (request.Length > MaxRequest)
        {
            var error = $"The number of requested blobs must not exceed {MaxRequest}";
            return ResultWrapper<GetBlobsV1Result>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        return ResultWrapper<GetBlobsV1Result>.Success(new GetBlobsV1Result(txPool.GetBlobsAndProofs(request)));
    }
}
