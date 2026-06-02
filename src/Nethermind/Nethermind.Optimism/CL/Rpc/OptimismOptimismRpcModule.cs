// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.CL.Decoding;
using Nethermind.Optimism.CL.L1Bridge;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.Proofs;

namespace Nethermind.Optimism.Cl.Rpc;

public class OptimismOptimismRpcModule(
    IEthApi l1Api,
    IL2Api l2Api,
    IExecutionEngineManager executionEngineManager,
    IDecodingPipeline decodingPipeline,
    CLChainSpecEngineParameters clParameters,
    OptimismChainSpecEngineParameters engineParameters,
    ChainSpec chainSpec
) : IOptimismOptimismRpcModule
{
    public async Task<ResultWrapper<OptimismOutputAtBlock>> optimism_outputAtBlock(ulong blockNumber)
    {
        ResultWrapper<OptimismSyncStatus> statusResult = await optimism_syncStatus();
        if (statusResult.Result.ResultType != ResultType.Success)
        {
            return ResultWrapper<OptimismOutputAtBlock>.Fail("Failed to get L2 block ref with sync status");
        }

        OptimismSyncStatus status = statusResult.Data;
        if (blockNumber > status.FinalizedL2.Number)
        {
            return ResultWrapper<OptimismOutputAtBlock>.Fail("Block is not finalized");
        }

        L2Block block = await l2Api.GetBlockByNumber(blockNumber);

        AccountProof? proof = await l2Api.GetProof(PreDeploys.L2ToL1MessagePasser, [], (long)block.Number);
        if (proof == null)
        {
            return ResultWrapper<OptimismOutputAtBlock>.Fail("Failed to get proof");
        }

        OptimismOutputV0 output = new()
        {
            StateRoot = block.StateRoot,
            MessagePasserStorageRoot = proof.StorageRoot,
            BlockHash = block.Hash
        };

        OptimismOutputAtBlock result = new()
        {
            Version = OptimismOutputV0.Version,
            OutputRoot = output.Root(),
            BlockRef = L2BlockRef.From(block),
            WithdrawalStorageRoot = output.MessagePasserStorageRoot,
            StateRoot = output.StateRoot,
            Status = status
        };

        return ResultWrapper<OptimismOutputAtBlock>.Success(result);
    }

    public Task<ResultWrapper<OptimismRollupConfig>> optimism_rollupConfig()
    {
        OptimismRollupConfig config = OptimismRollupConfig.Build(clParameters, engineParameters, chainSpec);
        return ResultWrapper<OptimismRollupConfig>.Success(config);
    }

    public async Task<ResultWrapper<OptimismSyncStatus>> optimism_syncStatus()
    {
        // TODO: We need to use `fullTxs` due to serialization issues

        L1BlockRef currentL1 = L1BlockRef.Zero;
        if (decodingPipeline.DecodedBatchesReader.TryPeek(out (BatchV1 Batch, ulong L1BatchOrigin) pendingBatch))
        {
            L1Block? currentL1Block = await l1Api.GetBlockByNumber(pendingBatch.L1BatchOrigin, fullTxs: true);
            // NOTE: If we got a batch from this block, then the L1 client must have it
            ArgumentNullException.ThrowIfNull(currentL1Block);
            currentL1 = L1BlockRef.From(currentL1Block.Value);
        }

        Task<L1Block?> headL1 = l1Api.GetHead(true);
        Task<L1Block?> safeL1 = l1Api.GetSafe(true);
        Task<L1Block?> finalizedL1 = l1Api.GetFinalized(true);

        // TODO: From `executionEngineManager` or `l2Api`?
        (BlockId Head, BlockId Finalized, BlockId Safe) currentL2Blocks = await executionEngineManager.GetCurrentBlocks();
        Task<L2Block> unsafeL2 = l2Api.GetBlockByNumber(currentL2Blocks.Head.Number);
        Task<L2Block> safeL2 = l2Api.GetBlockByNumber(currentL2Blocks.Safe.Number);
        Task<L2Block> finalizedL2 = l2Api.GetBlockByNumber(currentL2Blocks.Finalized.Number);

        OptimismSyncStatus syncStatus = new()
        {
            // L1
            CurrentL1 = currentL1,
            HeadL1 = L1BlockRef.From(await headL1),
            SafeL1 = L1BlockRef.From(await safeL1),
            FinalizedL1 = L1BlockRef.From(await finalizedL1),
            // L2
            UnsafeL2 = L2BlockRef.From(await unsafeL2),
            SafeL2 = L2BlockRef.From(await safeL2),
            FinalizedL2 = L2BlockRef.From(await finalizedL2),
            // TODO
            PendingSafeL2 = L2BlockRef.Zero,
            QueuedUnsafeL2 = L2BlockRef.Zero,
        };
        return ResultWrapper<OptimismSyncStatus>.Success(syncStatus);
    }

    public Task<ResultWrapper<string>> optimism_version() => ResultWrapper<string>.Success(ProductInfo.Version);
}
