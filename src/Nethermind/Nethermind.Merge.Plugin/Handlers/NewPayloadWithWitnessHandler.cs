// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

/// <remarks>
/// <see cref="IEngineRpcModule"/> is taken via <see cref="Lazy{T}"/> to break the construction cycle
/// (the module composes this handler).
/// </remarks>
public sealed class NewPayloadWithWitnessHandler(
    Lazy<IEngineRpcModule> engineModule,
    WitnessRendezvous rendezvous,
    ILogManager? logManager = null) : IAsyncHandler<ExecutionPayloadParams<ExecutionPayloadV4>, NewPayloadWithWitnessV1Result>
{
    private readonly ILogger _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<NewPayloadWithWitnessHandler>();

    public async Task<ResultWrapper<NewPayloadWithWitnessV1Result>> HandleAsync(ExecutionPayloadParams<ExecutionPayloadV4> request)
    {
        ExecutionPayloadV4 executionPayload = request.ExecutionPayload;
        Hash256? blockHash = executionPayload.BlockHash;

        if (blockHash is null)
        {
            if (_logger.IsWarn) _logger.Warn("engine_newPayloadWithWitness: payload BlockHash is null — rejecting as InvalidParams.");
            return ResultWrapper<NewPayloadWithWitnessV1Result>.Fail(
                "executionPayload.blockHash is required", ErrorCodes.InvalidParams);
        }

        using WitnessRequest witnessRequest = rendezvous.RequestWitness(blockHash);

        using ResultWrapper<PayloadStatusV1> statusResult = await engineModule.Value.engine_newPayloadV5(
            executionPayload, request.BlobVersionedHashes ?? [], request.ParentBeaconBlockRoot, request.ExecutionRequests);

        Witness? capturedWitness = witnessRequest.Task.IsCompletedSuccessfully ? witnessRequest.Task.Result : null;

        if (statusResult.Result.ResultType != ResultType.Success)
        {
            capturedWitness?.Dispose();
            return ResultWrapper<NewPayloadWithWitnessV1Result>.Fail(
                statusResult.Result.Error ?? "engine_newPayloadWithWitness: payload processing failed",
                statusResult.ErrorCode);
        }

        PayloadStatusV1 payloadStatus = statusResult.Data!;
        Witness? witness = null;

        if (payloadStatus.Status == PayloadStatus.Valid)
            witness = capturedWitness;
        else
            capturedWitness?.Dispose();

        return ResultWrapper<NewPayloadWithWitnessV1Result>.Success(
            NewPayloadWithWitnessV1Result.FromPayloadStatus(payloadStatus, witness));
    }
}
