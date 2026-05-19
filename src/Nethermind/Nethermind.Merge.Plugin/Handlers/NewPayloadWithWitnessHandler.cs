// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Blockchain;
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
/// The V5 execution step is supplied as a delegate so this handler has no dependency on
/// <see cref="EngineRpcModule"/>, neither a back-reference nor a test-driven interface on
/// the production type. In production the module passes <c>engine_newPayloadV5</c> as a
/// method-group; tests inject a plain lambda.
/// </remarks>
public sealed class NewPayloadWithWitnessHandler(
    Func<ExecutionPayloadV4, byte[]?[], Hash256?, byte[][]?, Task<ResultWrapper<PayloadStatusV1>>> newPayloadV5,
    IBlockTree blockTree,
    IWitnessGeneratingBlockProcessingEnvFactory witnessEnvFactory,
    ILogManager logManager) : INewPayloadWithWitnessHandler
{
    private readonly ILogger _logger = logManager.GetClassLogger<NewPayloadWithWitnessHandler>();

    public async Task<ResultWrapper<NewPayloadWithWitnessV1Result>> HandleAsync(
        ExecutionPayloadV4 executionPayload,
        byte[]?[] blobVersionedHashes,
        Hash256? parentBeaconBlockRoot,
        byte[][]? executionRequests)
    {
        ResultWrapper<PayloadStatusV1> statusResult = await newPayloadV5(
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
                // TODO(perf): TryGenerateWitnessForBlock re-executes the block via a second
                // WitnessCollector.GetWitnessForExistingBlock → ProcessOne call after
                // engine_newPayloadV5 has already processed it once. The parent spec
                // (execution-apis #773) was designed to eliminate this double-execution.
                // Wiring witness collection into the primary processing path is a follow-up.
                // https://github.com/NethermindEth/nethermind/issues/11636
                witness = TryGenerateWitnessForBlock(executionPayload);
                if (witness is null && _logger.IsError)
                {
                    _logger.Error(
                        $"engine_newPayloadWithWitness: payload is VALID but execution witness could not be generated " +
                        $"for block {executionPayload.BlockHash}. " +
                        $"The block has been accepted; returning witness=None per spec Union[None, T] arm.");
                }
            }

            return ResultWrapper<NewPayloadWithWitnessV1Result>.Success(
                NewPayloadWithWitnessV1Result.FromPayloadStatus(payloadStatus, witness));
        }
    }

    private Witness? TryGenerateWitnessForBlock(ExecutionPayloadV4 executionPayload)
    {
        BlockDecodingResult decodingResult = executionPayload.TryGetBlock();
        Block? block = decodingResult.Block;
        if (block is null)
        {
            if (_logger.IsWarn)
                _logger.Warn($"engine_newPayloadWithWitness: witness generation skipped — could not decode block from ExecutionPayloadV4 " +
                             $"(hash={executionPayload.BlockHash}). Decode error: {decodingResult.Error}");
            return null;
        }

        BlockHeader? parent = blockTree.FindHeader(
            block.ParentHash!,
            BlockTreeLookupOptions.DoNotCreateLevelIfMissing);
        if (parent is null)
        {
            if (_logger.IsWarn)
                _logger.Warn($"engine_newPayloadWithWitness: witness generation skipped — parent header not found for block " +
                             $"{block.Hash} (parentHash={block.ParentHash}).");
            return null;
        }

        try
        {
            using IWitnessGeneratingBlockProcessingEnvScope scope = witnessEnvFactory.CreateScope();
            IExistingBlockWitnessCollector collector = scope.Env.CreateExistingBlockWitnessCollector();
            return collector.GetWitnessForExistingBlock(parent, block);
        }
        catch (OperationCanceledException ex)
        {
            if (_logger.IsWarn)
                _logger.Warn($"engine_newPayloadWithWitness: witness generation cancelled for block {block.Hash}: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            if (_logger.IsError)
                _logger.Error($"engine_newPayloadWithWitness: witness generation failed for block {block.Hash}: {ex.Message}", ex);
            return null;
        }
    }
}
