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
/// cycle (the module composes this handler). The <see cref="WitnessCapturingWorldStateProxy"/>
/// is null on pre-Amsterdam chains where the capability is gated off; a well-behaved CL won't
/// call this method then.
/// </remarks>
public sealed class NewPayloadWithWitnessHandler(
    Lazy<IEngineRpcModule> engineModule,
    WitnessProxyResolver proxyResolver,
    ILogManager? logManager = null) : INewPayloadWithWitnessHandler
{
    private readonly ILogger _logger = (logManager ?? LimboLogs.Instance).GetClassLogger<NewPayloadWithWitnessHandler>();
    private readonly WitnessCapturingWorldStateProxy? proxy = proxyResolver.Proxy;

    public async Task<ResultWrapper<NewPayloadWithWitnessV1Result>> HandleAsync(
        ExecutionPayloadV4 executionPayload,
        byte[]?[] blobVersionedHashes,
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

        // Pre-Amsterdam: no proxy installed, forward to V5 and return witness-less success.
        Task<Witness?>? captureTask = proxy?.RequestWitness(blockHash);

        ResultWrapper<PayloadStatusV1> statusResult;
        try
        {
            statusResult = await engineModule.Value.engine_newPayloadV5(
                executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests);
        }
        catch
        {
            // Prevent the armed TCS from outliving the request.
            proxy?.CancelWitnessRequest(blockHash);
            throw;
        }

        using (statusResult)
        {
            if (statusResult.Result.ResultType != ResultType.Success)
            {
                proxy?.CancelWitnessRequest(blockHash);
                return ResultWrapper<NewPayloadWithWitnessV1Result>.Fail(
                    statusResult.Result.Error ?? "engine_newPayloadV5 failed",
                    statusResult.ErrorCode);
            }

            PayloadStatusV1 payloadStatus = statusResult.Data!;
            Witness? witness = null;

            if (payloadStatus.Status == PayloadStatus.Valid && captureTask is not null)
            {
                // BlockProcessor normally completes the TCS synchronously inside ProcessOne.
                // If it didn't, the block took an early-return path (already known, etc.) and
                // was never processed — cancel so the await below doesn't block forever.
                if (!captureTask.IsCompleted)
                    proxy!.CancelWitnessRequest(blockHash);

                try
                {
                    witness = await captureTask;
                }
                catch (OperationCanceledException)
                {
                    if (_logger.IsWarn) _logger.Warn($"engine_newPayloadWithWitness: witness capture cancelled for {blockHash}. Returning VALID with no witness.");
                }
            }
            else if (captureTask is not null)
            {
                proxy!.CancelWitnessRequest(blockHash);
                if (captureTask.IsCompletedSuccessfully)
                    (await captureTask)?.Dispose();
            }

            return ResultWrapper<NewPayloadWithWitnessV1Result>.Success(
                NewPayloadWithWitnessV1Result.FromPayloadStatus(payloadStatus, witness));
        }
    }
}
