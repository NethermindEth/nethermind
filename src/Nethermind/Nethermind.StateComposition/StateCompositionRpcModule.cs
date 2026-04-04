// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;

namespace Nethermind.StateComposition;

public class StateCompositionRpcModule(
    IStateCompositionService service,
    IStateCompositionStateHolder stateHolder,
    IBlockTree blockTree)
    : IStateCompositionRpcModule
{
    public async Task<ResultWrapper<StateCompositionStats>> statecomp_getStats(
        BlockParameter? blockParameter = null)
    {
        BlockHeader? header = ResolveHeader(blockParameter);
        if (header is null)
            return ResultWrapper<StateCompositionStats>.Fail("Block not found");

        try
        {
            Result<StateCompositionStats> result = await service.AnalyzeAsync(header, CancellationToken.None)
                .ConfigureAwait(false);
            return !result.Success(out StateCompositionStats stats, out var error)
                ? ResultWrapper<StateCompositionStats>.Fail(error, ErrorCodes.LimitExceeded)
                : ResultWrapper<StateCompositionStats>.Success(stats);
        }
        catch (OperationCanceledException)
        {
            return ResultWrapper<StateCompositionStats>.Fail("Scan was cancelled");
        }
    }

    public Task<ResultWrapper<CachedStatsResponse>> statecomp_getCachedStats(
        BlockParameter? blockParameter = null)
    {
        long? blockNum = ResolveBlockNumber(blockParameter);
        ScanCacheEntry? entry = stateHolder.GetScan(blockNum);

        CachedStatsResponse response = new()
        {
            Stats = entry?.Stats,
            AvailableScans = stateHolder.ListScans(),
        };

        return Task.FromResult(ResultWrapper<CachedStatsResponse>.Success(response));
    }

    public Task<ResultWrapper<IReadOnlyList<ScanMetadata>>> statecomp_listScans()
    {
        return Task.FromResult(
            ResultWrapper<IReadOnlyList<ScanMetadata>>.Success(stateHolder.ListScans()));
    }

    public async Task<ResultWrapper<TrieDepthDistribution>> statecomp_getTrieDistribution(
        BlockParameter? blockParameter = null)
    {
        long? blockNum = ResolveBlockNumber(blockParameter);
        Result<TrieDepthDistribution> result = await service.GetTrieDistributionAsync(blockNum)
            .ConfigureAwait(false);
        return !result.Success(out TrieDepthDistribution dist, out var error)
            ? ResultWrapper<TrieDepthDistribution>.Fail(error, ErrorCodes.ResourceUnavailable)
            : ResultWrapper<TrieDepthDistribution>.Success(dist);
    }

    public Task<ResultWrapper<bool>> statecomp_cancelScan()
    {
        service.CancelScan();
        return Task.FromResult(ResultWrapper<bool>.Success(true));
    }

    public async Task<ResultWrapper<TopContractEntry?>> statecomp_inspectContract(
        Address? address, BlockParameter? blockParameter = null)
    {
        if (address is null)
            return ResultWrapper<TopContractEntry?>.Fail("Address parameter is required", ErrorCodes.InvalidInput);

        BlockHeader? header = ResolveHeader(blockParameter);
        if (header is null)
            return ResultWrapper<TopContractEntry?>.Fail("Block not found");

        try
        {
            Result<TopContractEntry?> result = await service.InspectContractAsync(
                address, header, CancellationToken.None).ConfigureAwait(false);
            return !result.Success(out TopContractEntry? entry, out var error)
                ? ResultWrapper<TopContractEntry?>.Fail(error, ErrorCodes.LimitExceeded)
                : ResultWrapper<TopContractEntry?>.Success(entry);
        }
        catch (OperationCanceledException)
        {
            return ResultWrapper<TopContractEntry?>.Fail("Inspection was cancelled");
        }
    }

    public async Task<ResultWrapper<StateCompositionStats>> statecomp_scanByStateRoot(
        Hash256 stateRoot, long blockNumber)
    {
        BlockHeader header = new(
            parentHash: Keccak.Zero,
            unclesHash: Keccak.OfAnEmptySequenceRlp,
            beneficiary: Address.Zero,
            difficulty: UInt256.Zero,
            number: blockNumber,
            gasLimit: 0,
            timestamp: 0,
            extraData: [])
        {
            StateRoot = stateRoot,
        };

        try
        {
            Result<StateCompositionStats> result = await service.AnalyzeAsync(header, CancellationToken.None)
                .ConfigureAwait(false);
            return !result.Success(out StateCompositionStats stats, out var error)
                ? ResultWrapper<StateCompositionStats>.Fail(error, ErrorCodes.LimitExceeded)
                : ResultWrapper<StateCompositionStats>.Success(stats);
        }
        catch (OperationCanceledException)
        {
            return ResultWrapper<StateCompositionStats>.Fail("Scan was cancelled");
        }
    }

    private BlockHeader? ResolveHeader(BlockParameter? blockParameter)
    {
        if (blockParameter is null)
            return blockTree.Head?.Header;

        return blockTree.FindHeader(blockParameter);
    }

    private long? ResolveBlockNumber(BlockParameter? blockParameter)
    {
        if (blockParameter is null)
            return null;

        BlockHeader? header = blockTree.FindHeader(blockParameter);
        return header?.Number;
    }
}
