// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Db;
using Nethermind.Facade;
using Nethermind.Facade.Find;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.JsonRpc.Modules.LogIndex;

public class LogIndexRpcModule(ILogIndexStorage storage, ILogIndexBuilder builder, IBlockFinder blockFinder, IBlockchainBridge blockchainBridge) : ILogIndexRpcModule
{
    public ResultWrapper<Dictionary<byte[], int[]>> logIndex_keys(LogIndexKeysRequest request)
    {
        if (request.Address is { } address)
        {
            return ResultWrapper<Dictionary<byte[], int[]>>.Success(
                storage.GetKeysFor(address, GetBlockNumber(request.FromBlock), GetBlockNumber(request.ToBlock), request.IncludeValues)
            );
        }

        if (request.Topic is { } topic)
        {
            return ResultWrapper<Dictionary<byte[], int[]>>.Success(
                storage.GetKeysFor(request.TopicIndex ?? 0, topic, GetBlockNumber(request.FromBlock), GetBlockNumber(request.ToBlock), request.IncludeValues)
            );
        }

        return ResultWrapper<Dictionary<byte[], int[]>>.Fail("No address or topic specified.");
    }

    public ResultWrapper<int[]> logIndex_blockNumbers(Filter filter)
    {
        LogFilter logFilter = blockchainBridge.GetFilter(filter.FromBlock!, filter.ToBlock!, filter.Address, filter.Topics);

        return ResultWrapper<int[]>.Success(
            storage.GetBlockNumbersFor(logFilter, GetBlockNumber(logFilter.FromBlock), GetBlockNumber(logFilter.ToBlock)).ToArray()
        );
    }

    public ResultWrapper<LogIndexStatus> logIndex_status()
    {
        return ResultWrapper<LogIndexStatus>.Success(new()
        {
            Current = new()
            {
                FromBlock = storage.GetMinBlockNumber(),
                ToBlock = storage.GetMaxBlockNumber()
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
    }

    public ResultWrapper<bool> logIndex_compact(LogIndexCompactRequest request)
    {
        _ = Task.Run(async () => await storage.CompactAsync(request.Flush, request.MergeIterations, new(storage)));
        return ResultWrapper<bool>.Success(true);
    }

    private int GetBlockNumber(BlockParameter parameter)
    {
        if (parameter.BlockNumber is { } number)
            return (int)number;

        return (int?)blockFinder.FindBlock(parameter)?.Number ?? 0;
    }
}
