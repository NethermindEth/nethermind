// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// Concrete implementation of <see cref="INewPayloadWithWitnessHandler"/>.
/// </summary>
/// <remarks>
/// The V5 execution step is supplied as a delegate so this handler has no dependency on
/// <see cref="EngineRpcModule"/>, neither a back-reference nor a test-driven interface on
/// the production type. In production the module passes <c>engine_newPayloadV5</c> as a
/// method-group; tests inject a plain lambda.
/// </remarks>
public sealed class NewPayloadWithWitnessHandler(
    Func<ExecutionPayloadV4, byte[]?[], Hash256?, byte[][]?, Task<ResultWrapper<PayloadStatusV1>>> newPayloadV5,
    IWitnessCaptureRegistry witnessCaptureRegistry) : INewPayloadWithWitnessHandler
{
    public async Task<ResultWrapper<NewPayloadWithWitnessV1Result>> HandleAsync(
        ExecutionPayloadV4 executionPayload,
        byte[]?[] blobVersionedHashes,
        Hash256? parentBeaconBlockRoot,
        byte[][]? executionRequests)
    {
        Hash256? blockHash = executionPayload.BlockHash;

        Task<Witness?>? captureTask = blockHash is not null
            ? witnessCaptureRegistry.ArmCapture(blockHash)
            : null;

        ResultWrapper<PayloadStatusV1> statusResult = await newPayloadV5(
            executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests);

        using (statusResult)
        {
            if (statusResult.Result.ResultType != ResultType.Success)
            {
                if (blockHash is not null)
                    witnessCaptureRegistry.DisarmCapture(blockHash);

                return ResultWrapper<NewPayloadWithWitnessV1Result>.Fail(
                    statusResult.Result.Error ?? "engine_newPayloadV5 failed",
                    statusResult.ErrorCode);
            }

            PayloadStatusV1 payloadStatus = statusResult.Data!;
            Witness? witness = null;

            if (payloadStatus.Status == PayloadStatus.Valid)
            {
                if (captureTask is not null)
                {
                    witness = await captureTask;
                }
            }
            else
            {
                if (blockHash is not null)
                    witnessCaptureRegistry.DisarmCapture(blockHash);

                if (captureTask is not null && captureTask.IsCompletedSuccessfully)
                    (await captureTask)?.Dispose();
            }

            return ResultWrapper<NewPayloadWithWitnessV1Result>.Success(
                NewPayloadWithWitnessV1Result.FromPayloadStatus(payloadStatus, witness));
        }
    }
}
