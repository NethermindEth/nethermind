// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetPayloadBodiesByRangeV1Handler : IGetPayloadBodiesByRangeV1Handler
{
    protected const int MaxCount = 1024;

    protected readonly IBlockTree _blockTree;
    protected readonly ILogger _logger;

    public GetPayloadBodiesByRangeV1Handler(IBlockTree blockTree, ILogManager logManager)
    {
        _blockTree = blockTree;
        _logger = logManager.GetClassLogger();
    }

    protected bool CheckRangeCount(long start, long count, out string? error, out int errorCode)
    {
        if (start < 1 || count < 1)
        {
            error = $"'{nameof(start)}' and '{nameof(count)}' must be positive numbers";

            if (_logger.IsError) _logger.Error($"{GetType().Name}: ${error}");

            errorCode = ErrorCodes.InvalidParams;
            return false;
        }

        if (count > MaxCount)
        {
            error = $"The number of requested bodies must not exceed {MaxCount}";

            if (_logger.IsError) _logger.Error($"{GetType().Name}: {error}");

            errorCode = MergeErrorCodes.TooLargeRequest;
            return false;
        }
        error = null;
        errorCode = 0;
        return true;
    }

    public Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>> Handle(long start, long count)
    {
        if (!CheckRangeCount(start, count, out string? error, out int errorCode))
        {
            return ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>.Fail(error!, errorCode);
        }

        return ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>.Success(GetRequests(start, count));
    }

    private IEnumerable<ExecutionPayloadBodyV1Result?> GetRequests(long start, long count)
    {
        var headNumber = _blockTree.Head?.Number ?? 0;

        for (long i = start, c = Math.Min(start + count - 1, headNumber); i <= c; i++)
        {
            var block = _blockTree.FindBlock(i);

            yield return (block is null ? null : new ExecutionPayloadBodyV1Result(block.Transactions, block.Withdrawals));
        }

        yield break;
    }
}
