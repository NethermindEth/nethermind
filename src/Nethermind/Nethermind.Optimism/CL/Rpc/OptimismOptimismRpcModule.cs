// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.CL.L1Bridge;

namespace Nethermind.Optimism.Cl.Rpc;

public class OptimismOptimismRpcModule(
    IEthApi l1Api,
    IL2Api l2Api,
    IExecutionEngineManager executionEngineManager
) : IOptimismOptimismRpcModule
{
    public Task<ResultWrapper<int>> optimism_outputAtBlock()
    {
        return ResultWrapper<int>.Success(0);
    }

    public Task<ResultWrapper<int>> optimism_rollupConfig()
    {
        return ResultWrapper<int>.Success(0);
    }

    public async Task<ResultWrapper<OptimismSyncStatus>> optimism_syncStatus()
    {
        // TODO: We need to use `fullTxs` due to serialization issues
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
            // TODO: Nullables
            HeadL1 = L1BlockRef.From((await headL1).Value),
            SafeL1 = L1BlockRef.From((await safeL1).Value),
            FinalizedL1 = L1BlockRef.From((await finalizedL1).Value),
            // L2
            UnsafeL2 = L2BlockRef.From(await unsafeL2),
            SafeL2 = L2BlockRef.From(await safeL2),
            FinalizedL2 = L2BlockRef.From(await finalizedL2),
            // TODO
            CurrentL1 = null!,
            CurrentL1Finalized = null!,
            PendingSafeL2 = null!,
            QueuedUnsafeL2 = null!,
        };
        return ResultWrapper<OptimismSyncStatus>.Success(syncStatus);
    }

    public Task<ResultWrapper<string>> optimism_version()
    {
        return ResultWrapper<string>.Success(ProductInfo.Version);
    }
}
