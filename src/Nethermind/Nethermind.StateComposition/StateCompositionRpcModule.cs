// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
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
        Block? head = blockTree.Head;
        if (head is null)
            return ResultWrapper<StateCompositionStats>.Fail("No head block available");

        try
        {
            Result<StateCompositionStats> result = await service.AnalyzeAsync(head.Header, CancellationToken.None)
                .ConfigureAwait(false);
            return !result.Success(out StateCompositionStats stats, out var error) ?
                ResultWrapper<StateCompositionStats>.Fail(error, ErrorCodes.LimitExceeded) :
                ResultWrapper<StateCompositionStats>.Success(stats);
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
        Result<TrieDepthDistribution> result = await service.GetTrieDistributionAsync()
            .ConfigureAwait(false);
        return !result.Success(out TrieDepthDistribution dist, out var error) ?
            ResultWrapper<TrieDepthDistribution>.Fail(error, ErrorCodes.ResourceUnavailable) :
            ResultWrapper<TrieDepthDistribution>.Success(dist);
    }

    public Task<ResultWrapper<bool>> statecomp_cancelScan()
    {
        service.CancelScan();
        return Task.FromResult(ResultWrapper<bool>.Success(true));
    }

    public async Task<ResultWrapper<TopContractEntry?>> statecomp_inspectContract(Address? address)
    {
        if (address is null)
            return ResultWrapper<TopContractEntry?>.Fail("Address parameter is required", ErrorCodes.InvalidInput);

        Block? head = blockTree.Head;
        if (head is null)
            return ResultWrapper<TopContractEntry?>.Fail("No head block available");

        try
        {
            Result<TopContractEntry?> result = await service.InspectContractAsync(
                address, head.Header, CancellationToken.None).ConfigureAwait(false);
            return !result.Success(out TopContractEntry? entry, out var error) ? ResultWrapper<TopContractEntry?>.Fail(error, ErrorCodes.LimitExceeded) : ResultWrapper<TopContractEntry?>.Success(entry);
        }
        catch (OperationCanceledException)
        {
            return ResultWrapper<TopContractEntry?>.Fail("Inspection was cancelled");
        }
    }
}
