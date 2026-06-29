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
/// <see cref="IEngineRpcModule"/> is taken via <see cref="Lazy{T}"/> to break the construction
/// cycle (the module composes this handler). On pre-Amsterdam chains the
/// <see cref="WitnessCapturingBlockProcessor"/> decorator is not installed, so the rendezvous
/// registration for any requested block hash never completes — the <c>using</c> registration
/// cancels it on return, so the handler simply yields VALID with no witness.
/// </remarks>
public sealed class NewPayloadWithWitnessHandler(
    Lazy<IEngineRpcModule> engineModule,
    WitnessRendezvous rendezvous,
    ILogManager? logManager = null) : INewPayloadWithWitnessHandler
{
    private readonly ILogger _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<NewPayloadWithWitnessHandler>();

    public async Task<ResultWrapper<NewPayloadWithWitnessV1Result>> HandleAsync(
        ExecutionPayloadV4 executionPayload,
        Hash256?[] blobVersionedHashes,
        Hash256? parentBeaconBlockRoot,
        byte[][]? executionRequests)
    {
        Hash256? blockHash = executionPayload.BlockHash;

        if (blockHash is null)
        {
            if (_logger.IsWarn) _logger.Warn("engine_newPayloadWithWitness: payload BlockHash is null — rejecting as InvalidParams.");
            return ResultWrapper<NewPayloadWithWitnessV1Result>.Fail(
                "executionPayload.blockHash is required", ErrorCodes.InvalidParams);
        }

        // The using guarantees the rendezvous slot is removed and the capture task cancelled on every
        // exit path (exception, non-success, non-VALID, or VALID-but-not-captured).
        using WitnessRequest request = rendezvous.RequestWitness(blockHash);

        using ResultWrapper<PayloadStatusV1> statusResult = await engineModule.Value.engine_newPayloadV5(
            executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests);

        if (statusResult.Result.ResultType != ResultType.Success)
        {
            return ResultWrapper<NewPayloadWithWitnessV1Result>.Fail(
                statusResult.Result.Error ?? "engine_newPayloadV5 failed",
                statusResult.ErrorCode);
        }

        PayloadStatusV1 payloadStatus = statusResult.Data!;
        Witness? witness = null;

        // engine_newPayloadV5 returns only after ProcessOne has run, so the witness processor has
        // already completed the registration (happy path) — or it never will (the block took an
        // early-return path, or the decorator is absent pre-Amsterdam). Either way the task is in its
        // final state here, so a synchronous check suffices and the using-Dispose cleans up the rest.
        if (request.Task.IsCompletedSuccessfully)
        {
            if (payloadStatus.Status == PayloadStatus.Valid)
                witness = request.Task.Result;
            else
                // Non-VALID but a witness was still produced: we won't return it, so dispose to avoid a leak.
                request.Task.Result?.Dispose();
        }

        return ResultWrapper<NewPayloadWithWitnessV1Result>.Success(
            NewPayloadWithWitnessV1Result.FromPayloadStatus(payloadStatus, witness));
    }
}
