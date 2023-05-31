// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IAsyncHandler<byte[], GetPayloadV3Result?> _getPayloadHandlerV3;

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV3(ExecutionPayload executionPayload, byte[][]? blobVersionedHashes = null)
    {
        if (blobVersionedHashes is null)
        {
            string error = "Blob versioned hashes must be set";
            if (_logger.IsWarn) _logger.Warn(error);
            return ResultWrapper<PayloadStatusV1>.Success(
                new PayloadStatusV1
                {
                    Status = PayloadStatus.Invalid,
                    LatestValidHash = null,
                    ValidationError = error
                });
        }

        int index = 0;

        foreach (Transaction tx in executionPayload.GetTransactions())
        {
            if (!tx.SupportsBlobs || tx.BlobVersionedHashes is null)
            {
                continue;
            }

            foreach (byte[]? blobVersionedHash in tx.BlobVersionedHashes)
            {
                if (index == blobVersionedHashes.Length
                    || blobVersionedHash is null
                    || blobVersionedHashes[index] is null
                    || !blobVersionedHash.SequenceEqual(blobVersionedHashes[index]))
                {
                    string error = "Blob versioned hashes do not match";
                    if (_logger.IsWarn) _logger.Warn(error);
                    return ResultWrapper<PayloadStatusV1>.Success(
                        new PayloadStatusV1
                        {
                            Status = PayloadStatus.Invalid,
                            LatestValidHash = null,
                            ValidationError = error
                        });
                }
                index++;
            }
        }

        if (index != blobVersionedHashes.Length)
        {
            string error = "Blob versioned hashes do not match";
            if (_logger.IsWarn) _logger.Warn(error);
            return ResultWrapper<PayloadStatusV1>.Success(
                new PayloadStatusV1
                {
                    Status = PayloadStatus.Invalid,
                    LatestValidHash = null,
                    ValidationError = error
                });
        }

        return NewPayload(executionPayload, 3);
    }

    public async Task<ResultWrapper<GetPayloadV3Result?>> engine_getPayloadV3(byte[] payloadId) =>
        await _getPayloadHandlerV3.HandleAsync(payloadId);
}
