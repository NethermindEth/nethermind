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
/// TCS for any requested block hash never completes; the cancel-on-non-VALID and
/// cancel-when-not-completed branches below handle that gracefully.
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

        Task<Witness?> captureTask = rendezvous.RequestWitness(blockHash);

        ResultWrapper<PayloadStatusV1> statusResult;
        try
        {
            statusResult = await engineModule.Value.engine_newPayloadV5(
                executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests);
        }
        catch
        {
            rendezvous.CancelWitnessRequest(blockHash);
            throw;
        }

        using (statusResult)
        {
            if (statusResult.Result.ResultType != ResultType.Success)
            {
                rendezvous.CancelWitnessRequest(blockHash);
                return ResultWrapper<NewPayloadWithWitnessV1Result>.Fail(
                    statusResult.Result.Error ?? "engine_newPayloadV5 failed",
                    statusResult.ErrorCode);
            }

            PayloadStatusV1 payloadStatus = statusResult.Data!;
            Witness? witness = null;

            if (payloadStatus.Status == PayloadStatus.Valid)
            {
                // BlockProcessor normally completes the TCS synchronously inside ProcessOne.
                // If it didn't, the block either took an early-return path (already known, etc.)
                // or the decorator isn't installed (pre-Amsterdam) — cancel so the await below
                // doesn't block forever.
                if (!captureTask.IsCompleted)
                    rendezvous.CancelWitnessRequest(blockHash);

                try
                {
                    witness = await captureTask;
                }
                catch (OperationCanceledException)
                {
                    if (_logger.IsWarn) _logger.Warn($"engine_newPayloadWithWitness: witness capture cancelled for {blockHash}. Returning VALID with no witness.");
                }
            }
            else
            {
                rendezvous.CancelWitnessRequest(blockHash);
                if (captureTask.IsCompletedSuccessfully)
                    (await captureTask)?.Dispose();
            }

            return ResultWrapper<NewPayloadWithWitnessV1Result>.Success(
                NewPayloadWithWitnessV1Result.FromPayloadStatus(payloadStatus, witness));
        }
    }
}
