// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Immutable;
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

    public Task<ResultWrapper<ScanProgressResult>> statecomp_getScanProgress()
    {
        double progress = _stateHolder.ScanProgress;
        ScanMetadata? meta = _stateHolder.LastScanMetadata;

        // Estimate elapsed time during active scan
        TimeSpan? elapsed = _stateHolder.IsScanning && meta?.CompletedAt is DateTimeOffset lastCompleted
            ? DateTimeOffset.UtcNow - lastCompleted
            : null;

        // Estimate remaining time if we have meaningful progress
        TimeSpan? eta = progress > 0.01 && elapsed.HasValue
            ? TimeSpan.FromSeconds(elapsed.Value.TotalSeconds / progress * (1.0 - progress))
            : null;

        ScanProgressResult result = new()
        {
            IsScanning = _stateHolder.IsScanning,
            Progress = progress,
            EstimatedAccountsRemaining = null,
            ElapsedTime = elapsed,
            EstimatedTimeRemaining = eta,
        };

        return Task.FromResult(ResultWrapper<ScanProgressResult>.Success(result));
    }

    public Task<ResultWrapper<CachedStatsResponse>> statecomp_getCachedStats()
    {
        CachedStatsResponse response = new()
        {
            Stats = _stateHolder.IsInitialized ? _stateHolder.CurrentStats : null,
            BlocksSinceBaseline = _stateHolder.BlocksSinceBaseline,
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

    public Task<ResultWrapper<ModuleInfo>> statecomp_getModuleInfo()
    {
        ModuleInfo info = new()
        {
            Version = "1.0.0",
            Description = "State composition metrics for bloatnet benchmarking",
            Endpoints =
            [
                new EndpointInfo { Name = "statecomp_getStats", Description = "Run full state composition scan" },
                new EndpointInfo { Name = "statecomp_getScanProgress", Description = "Get scan progress during active scan" },
                new EndpointInfo { Name = "statecomp_getCachedStats", Description = "Get cached stats with staleness info" },
                new EndpointInfo { Name = "statecomp_getCacheMetadata", Description = "Get scan metadata" },
                new EndpointInfo { Name = "statecomp_getTrieDistribution", Description = "Get trie depth distribution" },
                new EndpointInfo { Name = "statecomp_getModuleInfo", Description = "Get module info and endpoint list" },
            ],
        };

        return Task.FromResult(ResultWrapper<ModuleInfo>.Success(info));
    }
}
