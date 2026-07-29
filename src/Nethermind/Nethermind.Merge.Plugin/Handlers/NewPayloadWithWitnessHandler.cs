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
    ILogManager? logManager = null) :
    IAsyncHandler<ExecutionPayloadParams<ExecutionPayloadV3>, NewPayloadWithWitnessV1Result>,
    IAsyncHandler<ExecutionPayloadParams<ExecutionPayloadV4>, NewPayloadWithWitnessV1Result>
{
    private readonly ILogger _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<NewPayloadWithWitnessHandler>();

    public Task<ResultWrapper<NewPayloadWithWitnessV1Result>> HandleAsync(ExecutionPayloadParams<ExecutionPayloadV3> request) =>
        HandleAsync(
            request,
            nameof(IEngineRpcModule.engine_newPayloadWithWitnessV4),
            static (module, payload) => module.engine_newPayloadV4(
                payload.ExecutionPayload,
                payload.BlobVersionedHashes!,
                payload.ParentBeaconBlockRoot,
                payload.ExecutionRequests));

    public Task<ResultWrapper<NewPayloadWithWitnessV1Result>> HandleAsync(ExecutionPayloadParams<ExecutionPayloadV4> request) =>
        HandleAsync(
            request,
            nameof(IEngineRpcModule.engine_newPayloadWithWitnessV5),
            static (module, payload) => module.engine_newPayloadV5(
                payload.ExecutionPayload,
                payload.BlobVersionedHashes!,
                payload.ParentBeaconBlockRoot,
                payload.ExecutionRequests));

    private async Task<ResultWrapper<NewPayloadWithWitnessV1Result>> HandleAsync<TExecutionPayload>(
        ExecutionPayloadParams<TExecutionPayload> request,
        string methodName,
        Func<IEngineRpcModule, ExecutionPayloadParams<TExecutionPayload>, Task<ResultWrapper<PayloadStatusV1>>> submitPayload)
        where TExecutionPayload : ExecutionPayload
    {
        TExecutionPayload executionPayload = request.ExecutionPayload;
        Hash256? blockHash = executionPayload.BlockHash;

        if (blockHash is null)
        {
            if (_logger.IsWarn) _logger.Warn($"{methodName}: payload BlockHash is null — rejecting as InvalidParams.");
            return ResultWrapper<NewPayloadWithWitnessV1Result>.Fail(
                "executionPayload.blockHash is required", ErrorCodes.InvalidParams);
        }

        using WitnessRequest witnessRequest = rendezvous.RequestWitness(blockHash);

        using ResultWrapper<PayloadStatusV1> statusResult = await submitPayload(engineModule.Value, request);

        Witness? capturedWitness = witnessRequest.Task.IsCompletedSuccessfully ? witnessRequest.Task.Result : null;

        if (statusResult.Result.ResultType != ResultType.Success)
        {
            capturedWitness?.Dispose();
            return ResultWrapper<NewPayloadWithWitnessV1Result>.Fail(
                statusResult.Result.Error ?? $"{methodName}: payload processing failed",
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
