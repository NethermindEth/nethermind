// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Optimism.CL;

namespace Nethermind.Optimism.Cl.Rpc;

public class OptimismOptimismRpcModule(
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
        // var currentL1Block =

        // TODO: From `executionEngineManager` or `l2Api`?
        var currentL2Blocks = await executionEngineManager.GetCurrentBlocks();
        var unsafeL2 = l2Api.GetBlockByNumber(currentL2Blocks.Head.Number);
        var safeL2 = l2Api.GetBlockByNumber(currentL2Blocks.Safe.Number);
        var finalizedL2 = l2Api.GetBlockByNumber(currentL2Blocks.Finalized.Number);

        var syncStatus = new OptimismSyncStatus
        {
            UnsafeL2 = L2BlockRef.From(await unsafeL2),
            SafeL2 = L2BlockRef.From(await safeL2),
            FinalizedL2 = L2BlockRef.From(await finalizedL2),
            // TODO L2
            PendingSafeL2 = null!,
            QueuedUnsafeL2 = null!,
        };
        return ResultWrapper<OptimismSyncStatus>.Success(null!);
    }

    public Task<ResultWrapper<string>> optimism_version()
    {
        return ResultWrapper<string>.Success(ProductInfo.Version);
    }
}
