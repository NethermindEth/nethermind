// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Find;
using Nethermind.Db;
using Nethermind.Facade.Find;

namespace Nethermind.JsonRpc.Modules.LogIndex;

public class LogIndexRpcModule(ILogIndexStorage storage, ILogIndexService service, IBlockFinder blockFinder) : ILogIndexRpcModule
{
    public ResultWrapper<Dictionary<byte[], int[]>> logIndex_keys(LogIndexKeysRequest request)
    {
        return ResultWrapper<Dictionary<byte[], int[]>>.Success(
            storage.GetKeysFor(request.Key, GetBlockNumber(request.FromBlock), GetBlockNumber(request.ToBlock), request.IncludeValues)
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
                FromBlock = service.GetMinTargetBlockNumber(),
                ToBlock = service.GetMaxTargetBlockNumber()
            },
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
