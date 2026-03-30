// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.JsonRpc;

namespace Nethermind.StateComposition;

public class StateCompositionRpcModule : IStateCompositionRpcModule
{
    private readonly IStateCompositionService _service;
    private readonly StateCompositionStateHolder _stateHolder;
    private readonly IBlockTree _blockTree;

    public StateCompositionRpcModule(
        IStateCompositionService service,
        StateCompositionStateHolder stateHolder,
        IBlockTree blockTree)
    {
        _service = service;
        _stateHolder = stateHolder;
        _blockTree = blockTree;
    }

    public async Task<ResultWrapper<StateCompositionStats>> statecomp_getStats()
    {
        Core.Block? head = _blockTree.Head;
        if (head is null)
            return ResultWrapper<StateCompositionStats>.Fail("No head block available");

        try
        {
            StateCompositionStats stats = await _service.AnalyzeAsync(head.Header, CancellationToken.None)
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
            Stats = _stateHolder.IsInitialized ? _stateHolder.CurrentStats : null,
        };

        return Task.FromResult(ResultWrapper<CachedStatsResponse>.Success(response));
    }

    public Task<ResultWrapper<ScanMetadata?>> statecomp_getCacheMetadata()
    {
        return Task.FromResult(
            ResultWrapper<ScanMetadata?>.Success(_stateHolder.LastScanMetadata));
    }

    public async Task<ResultWrapper<TrieDepthDistribution>> statecomp_getTrieDistribution()
    {
        Core.Block? head = _blockTree.Head;
        if (head is null)
            return ResultWrapper<TrieDepthDistribution>.Fail("No head block available");

        try
        {
            TrieDepthDistribution dist = await _service.GetTrieDistributionAsync(
                head.Header, CancellationToken.None).ConfigureAwait(false);
            return ResultWrapper<TrieDepthDistribution>.Success(dist);
        }
        catch (InvalidOperationException ex)
        {
            return ResultWrapper<TrieDepthDistribution>.Fail(ex.Message);
        }
    }
}
