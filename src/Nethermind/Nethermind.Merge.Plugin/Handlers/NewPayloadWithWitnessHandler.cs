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

        if (statusResult.Result.ResultType != ResultType.Success)
        {
            return ResultWrapper<NewPayloadWithWitnessV1Result>.Fail(
                statusResult.Result.Error ?? "engine_newPayloadV5 failed",
                statusResult.ErrorCode);
        }

        PayloadStatusV1 payloadStatus = statusResult.Data!;
        Witness? witness = null;

        // We can't just await the rendezvous task: an already-known block returns VALID without
        // re-processing, and INVALID/SYNCING blocks may never reach witness capture — in those cases the
        // task is never completed and is only cancelled by the using-Dispose below, so awaiting would
        // block. IsCompletedSuccessfully is the non-blocking probe; .Result on a completed task can't throw.
        if (witnessRequest.Task.IsCompletedSuccessfully)
        {
            Witness? captured = witnessRequest.Task.Result;
            if (payloadStatus.Status == PayloadStatus.Valid)
                witness = captured;
            else
                captured?.Dispose(); // stray witness for a non-VALID status: drop it to avoid leaking pooled buffers.
        }

        return ResultWrapper<NewPayloadWithWitnessV1Result>.Success(
            NewPayloadWithWitnessV1Result.FromPayloadStatus(payloadStatus, witness));
    }
}
