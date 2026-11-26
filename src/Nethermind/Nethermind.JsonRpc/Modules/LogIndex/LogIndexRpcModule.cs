// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Facade.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Db.LogIndex;
using Nethermind.Facade;
using Nethermind.Facade.Find;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.JsonRpc.Modules.LogIndex;

public class LogIndexRpcModule(ILogIndexStorage storage, ILogIndexBuilder builder, IBlockFinder blockFinder, IBlockchainBridge blockchainBridge, IJsonRpcConfig jsonRpcConfig)
    : ILogIndexRpcModule
{
    public ResultWrapper<IEnumerable<long>> logIndex_blockNumbers(Filter filter)
    {
        if (!storage.Enabled)
            return ResultWrapper<IEnumerable<long>>.Fail("Log index is disabled.", ErrorCodes.ResourceUnavailable);

        // Null when nothing is indexed yet or the index stopped at a reorg it could not revert and may hold stale blocks
        if (storage.MinBlockNumber is not { } indexFrom || storage.MaxBlockNumber is not { } indexTo)
            return ResultWrapper<IEnumerable<long>>.Fail("Log index is not available.", ErrorCodes.ResourceUnavailable);

        LogFilter logFilter = blockchainBridge.GetFilter(filter.FromBlock, filter.ToBlock, filter.Address, filter.Topics);

        // The log index can only narrow blocks by address or topic, so it cannot serve a filter that matches every block
        if (logFilter.AcceptsAnyBlock)
            return ResultWrapper<IEnumerable<long>>.Fail("Filter must specify at least one address or topic.", ErrorCodes.InvalidParams);

        if (GetBlockNumber(logFilter.FromBlock) is not { } from)
            return ResultWrapper<IEnumerable<long>>.Fail($"Block {logFilter.FromBlock} is not found.", ErrorCodes.UnknownBlockError);

        if (GetBlockNumber(logFilter.ToBlock) is not { } to)
            return ResultWrapper<IEnumerable<long>>.Fail($"Block {logFilter.ToBlock} is not found.", ErrorCodes.UnknownBlockError);

        // Postings outside the indexed range are not guaranteed to be canonical, so only the indexed part of the range
        // is served; unlike eth_getLogs, the rest is not scanned. Kept unsigned so block numbers above long.MaxValue
        // stay above the index. A range entirely outside the index ends up with from > to and yields no blocks.
        from = Math.Max(from, (ulong)indexFrom);
        to = Math.Min(to, (ulong)indexTo);

        // Enumerated here so storage failures become JSON-RPC errors instead of breaking response serialization,
        // stopping one block past the limit so a long indexed range is not loaded in full.
        // The pooled list is the result itself; the response returns it to the pool once serialized
        int maxResults = jsonRpcConfig.MaxLogsPerResponse;
        ArrayPoolList<long> blockNumbers = new(0);
        bool transferred = false;
        try
        {
            foreach (long blockNumber in storage.EnumerateBlockNumbersFor(logFilter, from, to))
            {
                if (maxResults > 0 && blockNumbers.Count == maxResults)
                    return ResultWrapper<IEnumerable<long>>.Fail($"Too many blocks requested. Max blocks per response is {maxResults}.", ErrorCodes.LimitExceeded);

                blockNumbers.Add(blockNumber);
            }

            transferred = true;
            return ResultWrapper<IEnumerable<long>>.Success(blockNumbers);
        }
        finally
        {
            if (!transferred) blockNumbers.Dispose();
        }
    }

    public ResultWrapper<LogIndexStatus> logIndex_status() => ResultWrapper<LogIndexStatus>.Success(new()
    {
        Current = new()
        {
            FromBlock = (ulong?)storage.MinBlockNumber,
            ToBlock = (ulong?)storage.MaxBlockNumber
        },
        Target = new()
        {
            FromBlock = builder.MinTargetBlockNumber,
            ToBlock = builder.MaxTargetBlockNumber
        },
        IsRunning = builder.IsRunning,
        LastUpdate = builder.LastUpdate,
        LastError = builder.LastError?.ToString(),
        DbSize = storage.GetDbSize()
    });

    private ulong? GetBlockNumber(BlockParameter parameter) =>
        parameter.BlockNumber ?? blockFinder.FindBlock(parameter)?.Number;
}
