// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IAsyncHandler<byte[], GetPayloadV3Result?> _getPayloadHandlerV3;

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV3(ExecutionPayloadV3 executionPayload, byte[]?[] blobVersionedHashes) =>
        ValidateFork(executionPayload) ?? PreValidatePayload(executionPayload, blobVersionedHashes) ?? NewPayload(executionPayload, 3);

    private ResultWrapper<PayloadStatusV1>? PreValidatePayload(ExecutionPayloadV3 executionPayload, byte[]?[] blobVersionedHashes)
    {
        ResultWrapper<PayloadStatusV1> ErrorResult(string error)
        {
            if (_logger.IsWarn) _logger.Warn(error);
            return ResultWrapper<PayloadStatusV1>.Success(
                new PayloadStatusV1
                {
                    Status = PayloadStatus.Invalid,
                    LatestValidHash = null,
                    ValidationError = error
                });
        }

        static IEnumerable<byte[]?> FlattenHashesFromTransactions(ExecutionPayloadV3 payload) =>
            payload.GetTransactions()
                .Where(t => t.BlobVersionedHashes is not null)
                .SelectMany(t => t.BlobVersionedHashes!);

        return !FlattenHashesFromTransactions(executionPayload).SequenceEqual(blobVersionedHashes, Bytes.NullableEqualityComparer)
            ? ErrorResult("Blob versioned hashes do not match")
            : null;
    }

    private ResultWrapper<PayloadStatusV1>? ValidateFork(ExecutionPayloadV3 executionPayload)
    {
        if (executionPayload.ValidateFork(_specProvider))
        {
            return null;
        }
        else
        {
            if (_logger.IsWarn) _logger.Warn($"The payload is not supported by the current fork");
            return ResultWrapper<PayloadStatusV1>.Fail("unsupported fork", ErrorCodes.UnsupportedFork);
        }
    }

    public async Task<ResultWrapper<GetPayloadV3Result?>> engine_getPayloadV3(byte[] payloadId) =>
        await _getPayloadHandlerV3.HandleAsync(payloadId);
}
