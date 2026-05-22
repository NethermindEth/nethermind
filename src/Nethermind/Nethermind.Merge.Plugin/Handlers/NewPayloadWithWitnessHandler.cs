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

/// <summary>
/// Concrete implementation of <see cref="INewPayloadWithWitnessHandler"/>.
/// </summary>
/// <remarks>
/// Takes <see cref="IEngineRpcModule"/> via <see cref="Lazy{T}"/> to break the
/// construction cycle (the module composes this handler).
/// </remarks>
public sealed class NewPayloadWithWitnessHandler(
    Lazy<IEngineRpcModule> engineModule,
    IWitnessCaptureRegistry witnessCaptureRegistry,
    ILogManager? logManager = null) : INewPayloadWithWitnessHandler
{
    private readonly ILogger _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<NewPayloadWithWitnessHandler>();

    public async Task<ResultWrapper<NewPayloadWithWitnessV1Result>> HandleAsync(
        ExecutionPayloadV4 executionPayload,
        byte[]?[] blobVersionedHashes,
        Hash256? parentBeaconBlockRoot,
        byte[][]? executionRequests)
    {
        Hash256? blockHash = executionPayload.BlockHash;

        // A null BlockHash is unambiguously a malformed JSON-RPC payload: there is no way
        // to key the capture registry, and engine_newPayloadV5 would itself reject it.
        // Fail fast with InvalidParams so the caller gets a precise diagnosis.
        if (blockHash is null)
        {
            if (_logger.IsWarn)
                _logger.Warn("engine_newPayloadWithWitness: payload BlockHash is null — rejecting as InvalidParams.");
            return ResultWrapper<NewPayloadWithWitnessV1Result>.Fail(
                "executionPayload.blockHash is required", ErrorCodes.InvalidParams);
        }

        Task<Witness?> captureTask = witnessCaptureRegistry.ArmCapture(blockHash);

        ResultWrapper<PayloadStatusV1> statusResult = await engineModule.Value.engine_newPayloadV5(
            executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests);

        using (statusResult)
        {
            if (statusResult.Result.ResultType != ResultType.Success)
            {
                witnessCaptureRegistry.DisarmCapture(blockHash);
                return ResultWrapper<NewPayloadWithWitnessV1Result>.Fail(
                    statusResult.Result.Error ?? "engine_newPayloadV5 failed",
                    statusResult.ErrorCode);
            }

            PayloadStatusV1 payloadStatus = statusResult.Data!;
            Witness? witness = null;

            if (payloadStatus.Status == PayloadStatus.Valid)
            {
                // Invariant: BranchProcessor completes the TCS synchronously inside ProcessOne
                // before engine_newPayloadV5 returns. If captureTask is still pending here, the
                // block went through an early-return path (already-known, etc.) and was never
                // processed — disarm so the await does not block forever.
                if (!captureTask.IsCompleted)
                    witnessCaptureRegistry.DisarmCapture(blockHash);

                try
                {
                    witness = await captureTask;
                }
                catch (OperationCanceledException)
                {
                    // A concurrent ArmCapture for the same blockHash cancelled our task, OR
                    // we just disarmed because BranchProcessor did not run. Either way the
                    // block executed successfully — return VALID with a null witness.
                    if (_logger.IsWarn)
                        _logger.Warn($"engine_newPayloadWithWitness: witness capture cancelled for {blockHash}. Returning VALID with no witness.");
                }
            }
            else
            {
                witnessCaptureRegistry.DisarmCapture(blockHash);
                if (captureTask.IsCompletedSuccessfully)
                    (await captureTask)?.Dispose();
            }

            return ResultWrapper<NewPayloadWithWitnessV1Result>.Success(
                NewPayloadWithWitnessV1Result.FromPayloadStatus(payloadStatus, witness));
        }
    }
}
