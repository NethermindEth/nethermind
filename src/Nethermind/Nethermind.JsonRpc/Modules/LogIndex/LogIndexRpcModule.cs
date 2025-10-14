// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Db.LogIndex;
using Nethermind.Facade;
using Nethermind.Facade.Find;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.JsonRpc.Modules.LogIndex;

public class LogIndexRpcModule(ILogIndexStorage storage, ILogIndexBuilder builder, IBlockFinder blockFinder, IBlockchainBridge blockchainBridge)
    : ILogIndexRpcModule
{
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

    private int GetBlockNumber(BlockParameter parameter)
    {
        if (parameter.BlockNumber is { } number)
            return (int)number;

        return (int?)blockFinder.FindBlock(parameter)?.Number ?? 0;
    }
}
