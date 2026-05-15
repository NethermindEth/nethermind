// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin;

public partial class EngineRpcModule : IEngineRpcModule
{
    private readonly IAsyncHandler<byte[], GetPayloadV6Result?> _getPayloadHandlerV6 = getPayloadHandlerV6;
    private readonly IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV2Result?>> _executionGetPayloadBodiesByHashV2Handler = getPayloadBodiesByHashV2Handler;
    private readonly IGetPayloadBodiesByRangeV2Handler _executionGetPayloadBodiesByRangeV2Handler = getPayloadBodiesByRangeV2Handler;

    public Task<ResultWrapper<GetPayloadV6Result?>> engine_getPayloadV6(byte[] payloadId)
        => _getPayloadHandlerV6.HandleAsync(payloadId);

    public Task<ResultWrapper<PayloadStatusV1>> engine_newPayloadV5(
        ExecutionPayloadV4 executionPayload,
        byte[]?[] blobVersionedHashes,
        Hash256? parentBeaconBlockRoot,
        byte[][]? executionRequests)
        => NewPayload(
            new ExecutionPayloadParams<ExecutionPayloadV4>(executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests),
            EngineApiVersions.NewPayload.V5);

    public async Task<ResultWrapper<NewPayloadWithWitnessV1Result>> engine_newPayloadWithWitness(
        ExecutionPayloadV4 executionPayload,
        byte[]?[] blobVersionedHashes,
        Hash256? parentBeaconBlockRoot,
        byte[][]? executionRequests)
    {
        ResultWrapper<PayloadStatusV1> statusResult = await engine_newPayloadV5(
            executionPayload, blobVersionedHashes, parentBeaconBlockRoot, executionRequests);

        using (statusResult)
        {
            if (statusResult.Result.ResultType != ResultType.Success)
            {
                return ResultWrapper<NewPayloadWithWitnessV1Result>.Fail(
                    statusResult.Result.Error ?? "engine_newPayloadV5 failed",
                    statusResult.ErrorCode);
            }

            PayloadStatusV1 payloadStatus = statusResult.Data!;
            Witness? witness = null;

            if (payloadStatus.Status == PayloadStatus.Valid)
            {
                witness = TryGenerateWitnessForBlock(executionPayload);
                if (witness is null && _logger.IsWarn)
                    _logger.Warn("engine_newPayloadWithWitness: payload is VALID but execution witness could not be generated.");
            }

            return ResultWrapper<NewPayloadWithWitnessV1Result>.Success(
                NewPayloadWithWitnessV1Result.FromPayloadStatus(payloadStatus, witness));
        }
    }

    public Task<ResultWrapper<ForkchoiceUpdatedV1Result>> engine_forkchoiceUpdatedV4(
        ForkchoiceStateV1 forkchoiceState,
        PayloadAttributes? payloadAttributes = null)
        => ForkchoiceUpdated(forkchoiceState, payloadAttributes, EngineApiVersions.Fcu.V4);

    public Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByHashV2(
        IReadOnlyList<Hash256> blockHashes)
        => _executionGetPayloadBodiesByHashV2Handler.Handle(blockHashes);

    public Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> engine_getPayloadBodiesByRangeV2(
        long start,
        long count)
        => _executionGetPayloadBodiesByRangeV2Handler.Handle(start, count);

    private Witness? TryGenerateWitnessForBlock(ExecutionPayloadV4 executionPayload)
    {
        BlockDecodingResult decodingResult = executionPayload.TryGetBlock();
        Block? block = decodingResult.Block;
        if (block is null) return null;

        BlockHeader? parent = _blockTree.FindHeader(
            block.ParentHash!,
            BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
        if (parent is null) return null;

        try
        {
            using IWitnessGeneratingBlockProcessingEnvScope scope = _witnessEnvFactory.CreateScope();
            IExistingBlockWitnessCollector collector = scope.Env.CreateExistingBlockWitnessCollector();
            return collector.GetWitnessForExistingBlock(parent, block);
        }
        catch
        {
            return null;
        }
    }
}
