// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.JsonRpc;

using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Service;
using Nethermind.StateComposition.Snapshots;

namespace Nethermind.StateComposition.Rpc;

internal sealed class StateCompositionRpcModule(
    StateCompositionService service,
    StateCompositionStateHolder stateHolder,
    IBlockTree blockTree,
    StateCompositionSnapshotStore snapshotStore)
    : IStateCompositionRpcModule
{
    public Task<ResultWrapper<CachedStatsResponse>> statecomp_getCachedStats()
    {
        return Task.FromResult(
            ResultWrapper<CachedStatsResponse>.Success(stateHolder.BuildCachedStatsResponse()));
    }

    public Task<ResultWrapper<TrieDepthDistribution>> statecomp_getTrieDistribution()
    {
        Result<TrieDepthDistribution> result = service.GetTrieDistribution();
        return Task.FromResult(!result.Success(out TrieDepthDistribution dist, out string? error) ?
            ResultWrapper<TrieDepthDistribution>.Fail(error, ErrorCodes.ResourceUnavailable) :
            ResultWrapper<TrieDepthDistribution>.Success(dist));
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
            return ResultWrapper<TopContractEntry?>.Fail("No head block available", ErrorCodes.ResourceUnavailable);

        try
        {
            Result<TopContractEntry?> result = await service.InspectContractAsync(
                address, head.Header, CancellationToken.None).ConfigureAwait(false);
            return !result.Success(out TopContractEntry? entry, out string? error)
                ? ResultWrapper<TopContractEntry?>.Fail(error, ErrorCodes.ResourceUnavailable)
                : ResultWrapper<TopContractEntry?>.Success(entry);
        }
        catch (OperationCanceledException)
        {
            return ResultWrapper<TopContractEntry?>.Fail("Inspection was cancelled");
        }
    }

    public Task<ResultWrapper<StateCompositionSnapshot?>> statecomp_getStatsAtBlock(long blockNumber)
    {
        if (blockNumber < 0)
            return Task.FromResult(
                ResultWrapper<StateCompositionSnapshot?>.Fail("Block number must be non-negative", ErrorCodes.InvalidInput));

        StateCompositionSnapshot? snapshot = snapshotStore.ReadSnapshot(blockNumber);
        return Task.FromResult(ResultWrapper<StateCompositionSnapshot?>.Success(snapshot));
    }
}
