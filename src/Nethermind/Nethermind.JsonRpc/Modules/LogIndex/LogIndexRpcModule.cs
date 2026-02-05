// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Filters;
using Nethermind.Blockchain.Find;
using Nethermind.Db.LogIndex;
using Nethermind.Facade;
using Nethermind.Facade.Filters;
using Nethermind.Facade.Find;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.JsonRpc.Modules.LogIndex;

public class LogIndexRpcModule(ILogIndexStorage storage, ILogIndexBuilder builder, IBlockFinder blockFinder, IBlockchainBridge blockchainBridge)
    : ILogIndexRpcModule
{
    public ResultWrapper<IEnumerable<long>> logIndex_blockNumbers(Filter filter)
    {
        LogFilter logFilter = blockchainBridge.GetFilter(filter.FromBlock, filter.ToBlock, filter.Address, filter.Topics);

        if (GetBlockNumber(logFilter.FromBlock) is not { } from)
            return ResultWrapper<IEnumerable<long>>.Fail($"Block {logFilter.FromBlock} is not found.", ErrorCodes.UnknownBlockError);

        if (GetBlockNumber(logFilter.ToBlock) is not { } to)
            return ResultWrapper<IEnumerable<long>>.Fail($"Block {logFilter.ToBlock} is not found.", ErrorCodes.UnknownBlockError);

        return ResultWrapper<IEnumerable<long>>.Success(storage.EnumerateBlockNumbersFor(logFilter, from, to));
    }

    public ResultWrapper<LogIndexStatus> logIndex_status()
    {
        return ResultWrapper<LogIndexStatus>.Success(new()
        {
            Current = new()
            {
                FromBlock = storage.MinBlockNumber,
                ToBlock = storage.MaxBlockNumber
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

    private int? GetBlockNumber(BlockParameter parameter) =>
        parameter.BlockNumber is { } number
            ? (int)number
            : (int?)blockFinder.FindBlock(parameter)?.Number;
}
