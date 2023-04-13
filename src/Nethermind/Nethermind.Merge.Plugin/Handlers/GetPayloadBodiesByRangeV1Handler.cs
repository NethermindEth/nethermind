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
    private const int MaxCount = 1024;

    private readonly IBlockTree _blockTree;
    private readonly ILogger _logger;

    public GetPayloadBodiesByRangeV1Handler(IBlockTree blockTree, ILogManager logManager)
    {
        _blockTree = blockTree;
        _logger = logManager.GetClassLogger();
    }

    public Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>> Handle(long start, long count)
    {
        if (start < 1 || count < 1)
        {
            var error = $"'{nameof(start)}' and '{nameof(count)}' must be positive numbers";

            if (_logger.IsError) _logger.Error($"{nameof(GetPayloadBodiesByRangeV1Handler)}: ${error}");

            return ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>.Fail(error, ErrorCodes.InvalidParams);
        }

        if (count > MaxCount)
        {
            var error = $"The number of requested bodies must not exceed {MaxCount}";

            if (_logger.IsError) _logger.Error($"{nameof(GetPayloadBodiesByRangeV1Handler)}: {error}");

            return ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        var headNumber = _blockTree.Head?.Number ?? 0;
        var payloadBodies = new List<ExecutionPayloadBodyV1Result?>((int)count);

        for (long i = start, c = Math.Min(start + count - 1, headNumber); i <= c; i++)
        {
            var block = _blockTree.FindBlock(i);

            payloadBodies.Add(block is null ? null : new(block.Transactions, block.Withdrawals));
        }

        return ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>.Success(payloadBodies);
    }
}
