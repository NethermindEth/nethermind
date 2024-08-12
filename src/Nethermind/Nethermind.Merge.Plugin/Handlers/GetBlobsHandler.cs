// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetBlobsHandler(ITxPool txPool) : IAsyncHandler<byte[][], GetBlobsV1Result>
{
    private const int MaxRequest = 128;

    private readonly ConcurrentDictionary<byte[], List<Hash256>> _blobIndex = txPool.GetBlobIndex();

    public Task<ResultWrapper<GetBlobsV1Result>> HandleAsync(byte[][] request)
    {
        if (request.Length > MaxRequest)
        {
            var error = $"The number of requested blobs must not exceed {MaxRequest}";
            return ResultWrapper<GetBlobsV1Result>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        ArrayPoolList<BlobAndProofV1?> blobsAndProofs = new(request.Length);

        foreach (byte[] requestedBlobVersionedHash in request)
        {
            bool isBlobFound = false;
            if (_blobIndex.TryGetValue(requestedBlobVersionedHash, out List<Hash256>? txHashes)
                && txPool.TryGetPendingBlobTransaction(txHashes.First(), out Transaction? blobTx)
                && blobTx.BlobVersionedHashes?.Length > 0)
            {
                for (int indexOfBlob = 0; indexOfBlob < blobTx.BlobVersionedHashes.Length; indexOfBlob++)
                {
                    if (blobTx.BlobVersionedHashes[indexOfBlob] == requestedBlobVersionedHash)
                    {
                        isBlobFound = true;
                        blobsAndProofs.Add(new BlobAndProofV1(blobTx, indexOfBlob));
                        break;
                    }
                }
            }

            if (!isBlobFound)
            {
                blobsAndProofs.Add(null);
            }
        }

        return ResultWrapper<GetBlobsV1Result>.Success(new GetBlobsV1Result(blobsAndProofs.ToArray()));
    }
}
