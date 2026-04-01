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
            var result = await service.AnalyzeAsync(head.Header, CancellationToken.None)
                .ConfigureAwait(false);
            if (!result.Success(out var stats, out var error))
                return ResultWrapper<StateCompositionStats>.Fail(error, ErrorCodes.LimitExceeded);
            return ResultWrapper<StateCompositionStats>.Success(stats);
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

        var result = await service.GetTrieDistributionAsync(
            head.Header, CancellationToken.None).ConfigureAwait(false);
        if (!result.Success(out var dist, out var error))
            return ResultWrapper<TrieDepthDistribution>.Fail(error, ErrorCodes.ResourceUnavailable);
        return ResultWrapper<TrieDepthDistribution>.Success(dist);
    }

    public Task<ResultWrapper<bool>> statecomp_cancelScan()
    {
        service.CancelScan();
        return Task.FromResult(ResultWrapper<bool>.Success(true));
    }

    public async Task<ResultWrapper<TopContractEntry?>> statecomp_inspectContract(Core.Address address)
    {
        // H2: Validate address parameter before processing.
        if (address is null)
            return ResultWrapper<TopContractEntry?>.Fail("Address parameter is required", ErrorCodes.InvalidInput);

        Core.Block? head = blockTree.Head;
        if (head is null)
            return ResultWrapper<TopContractEntry?>.Fail("No head block available");

        try
        {
            var result = await service.InspectContractAsync(
                address, head.Header, CancellationToken.None).ConfigureAwait(false);
            if (!result.Success(out var entry, out var error))
                return ResultWrapper<TopContractEntry?>.Fail(error, ErrorCodes.LimitExceeded);
            return ResultWrapper<TopContractEntry?>.Success(entry);
        }
        catch (OperationCanceledException)
        {
            return ResultWrapper<TopContractEntry?>.Fail("Inspection was cancelled");
        }
    }
}
