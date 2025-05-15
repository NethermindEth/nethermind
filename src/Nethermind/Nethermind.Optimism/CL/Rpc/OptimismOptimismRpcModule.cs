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
    public Task<ResultWrapper<int>> optimism_outputAtBlock()
    {
        return ResultWrapper<int>.Success(0);
    }

    public Task<ResultWrapper<OptimismRollupConfig>> optimism_rollupConfig()
    {
        var config = OptimismRollupConfig.Build(clParameters, engineParameters, chainSpec);
        return ResultWrapper<OptimismRollupConfig>.Success(config);
    }

    public async Task<ResultWrapper<OptimismSyncStatus>> optimism_syncStatus()
    {
        // TODO: We need to use `fullTxs` due to serialization issues

        var currentL1 = L1BlockRef.Zero;
        if (decodingPipeline.DecodedBatchesReader.TryPeek(out var pendingBatch))
        {
            var currentL1Block = await l1Api.GetBlockByNumber(pendingBatch.L1OriginNum, fullTxs: true);
            // NOTE: If we got a batch from this block, then the L1 client must have it
            ArgumentNullException.ThrowIfNull(currentL1Block);
            currentL1 = L1BlockRef.From(currentL1Block.Value);
        }

        var headL1 = l1Api.GetHead(true);
        var safeL1 = l1Api.GetSafe(true);
        var finalizedL1 = l1Api.GetFinalized(true);

        // TODO: From `executionEngineManager` or `l2Api`?
        var currentL2Blocks = await executionEngineManager.GetCurrentBlocks();
        var unsafeL2 = l2Api.GetBlockByNumber(currentL2Blocks.Head.Number);
        var safeL2 = l2Api.GetBlockByNumber(currentL2Blocks.Safe.Number);
        var finalizedL2 = l2Api.GetBlockByNumber(currentL2Blocks.Finalized.Number);

        var syncStatus = new OptimismSyncStatus
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

    public Task<ResultWrapper<string>> optimism_version()
    {
        return ResultWrapper<string>.Success(ProductInfo.Version);
    }
}
