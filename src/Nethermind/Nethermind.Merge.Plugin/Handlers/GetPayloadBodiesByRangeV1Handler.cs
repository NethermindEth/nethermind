// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

public abstract class GetPayloadBodiesByRangeHandler<TResult>(IBlockTree blockTree, ILogManager logManager)
    where TResult : class
{
    private const int MaxCount = 1024;

    private readonly ILogger _logger = logManager.GetClassLogger();

    public Task<ResultWrapper<IEnumerable<TResult?>>> Handle(long start, long count)
    {
        if (start < 1 || count < 1)
        {
            const string error = $"'{nameof(start)}' and '{nameof(count)}' must be positive numbers";
            if (_logger.IsError) _logger.Error($"{GetType().Name}: ${error}");
            return ResultWrapper<IEnumerable<TResult?>>.Fail(error, ErrorCodes.InvalidParams);
        }

        if (count > MaxCount)
        {
            string error = $"The number of requested bodies must not exceed {MaxCount}";
            if (_logger.IsError) _logger.Error($"{GetType().Name}: {error}");
            return ResultWrapper<IEnumerable<TResult?>>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        return ResultWrapper<IEnumerable<TResult?>>.Success(GetRequests(start, count));
    }

    protected abstract TResult? CreateResult(Block block);

    private IEnumerable<TResult?> GetRequests(long start, long count)
    {
        long headNumber = blockTree.Head?.Number ?? 0;

        for (long i = start, c = Math.Min(start + count - 1, headNumber); i <= c; i++)
        {
            Block? block = blockTree.FindBlock(i);
            yield return block is null ? null : CreateResult(block);
        }
    }
}

public class GetPayloadBodiesByRangeV1Handler(IBlockTree blockTree, ILogManager logManager)
    : GetPayloadBodiesByRangeHandler<ExecutionPayloadBodyV1Result>(blockTree, logManager), IGetPayloadBodiesByRangeV1Handler
{
    protected override ExecutionPayloadBodyV1Result CreateResult(Block block) =>
        new(block.Transactions, block.Withdrawals);
}
