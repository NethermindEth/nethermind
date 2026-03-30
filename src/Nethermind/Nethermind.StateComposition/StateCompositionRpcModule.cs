// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.JsonRpc;

namespace Nethermind.StateComposition;

public class StateCompositionRpcModule(
    IStateCompositionService service,
    IStateCompositionStateHolder stateHolder,
    IBlockTree blockTree)
    : IStateCompositionRpcModule
{
    public async Task<ResultWrapper<StateCompositionStats>> statecomp_getStats()
    {
        Core.Block? head = blockTree.Head;
        if (head is null)
            return ResultWrapper<StateCompositionStats>.Fail("No head block available");

        try
        {
            StateCompositionStats stats = await service.AnalyzeAsync(head.Header, CancellationToken.None)
                .ConfigureAwait(false);
            return ResultWrapper<StateCompositionStats>.Success(stats);
        }
        catch (InvalidOperationException ex)
        {
            return ResultWrapper<StateCompositionStats>.Fail(ex.Message);
        }
        catch (OperationCanceledException)
        {
            return ResultWrapper<StateCompositionStats>.Fail("Scan was cancelled");
        }
    }

    public Task<ResultWrapper<CachedStatsResponse>> statecomp_getCachedStats()
    {
        CachedStatsResponse response = new()
        {
            Stats = stateHolder.IsInitialized ? stateHolder.CurrentStats : null,
        };

        return Task.FromResult(ResultWrapper<CachedStatsResponse>.Success(response));
    }

    public Task<ResultWrapper<ScanMetadata?>> statecomp_getCacheMetadata()
    {
        return Task.FromResult(
            ResultWrapper<ScanMetadata?>.Success(stateHolder.LastScanMetadata));
    }

    public async Task<ResultWrapper<TrieDepthDistribution>> statecomp_getTrieDistribution()
    {
        Core.Block? head = blockTree.Head;
        if (head is null)
            return ResultWrapper<TrieDepthDistribution>.Fail("No head block available");

        try
        {
            TrieDepthDistribution dist = await service.GetTrieDistributionAsync(
                head.Header, CancellationToken.None).ConfigureAwait(false);
            return ResultWrapper<TrieDepthDistribution>.Success(dist);
        }
        catch (InvalidOperationException ex)
        {
            return ResultWrapper<TrieDepthDistribution>.Fail(ex.Message);
        }
    }

    public Task<ResultWrapper<bool>> statecomp_cancelScan()
    {
        service.CancelScan();
        return Task.FromResult(ResultWrapper<bool>.Success(true));
    }
}
