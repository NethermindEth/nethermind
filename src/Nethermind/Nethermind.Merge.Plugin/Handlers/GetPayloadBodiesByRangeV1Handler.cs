// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
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

        var bestSuggestedNumber = _blockTree.BestSuggestedBody?.Number ?? long.MaxValue;
        var payloadBodies = new List<ExecutionPayloadBodyV1Result?>();
        var skipFrom = 0;
        var j = 0;

        for (long i = start, c = Math.Min(start + count, bestSuggestedNumber); i < c; i++, j++)
        {
            var block = _blockTree.FindBlock(i);

            if (block is null)
            {
                payloadBodies.Add(null);

                if (skipFrom == 0 && j > 0 && payloadBodies[j - 1] is not null)
                    skipFrom = j;
            }
            else
            {
                payloadBodies.Add(new(block.Transactions, block.Withdrawals));
                skipFrom = 0;
            }
        }

        var trimmedBodies = skipFrom > 0 ? payloadBodies.SkipLast(payloadBodies.Count - skipFrom) : payloadBodies;

        return ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>.Success(trimmedBodies);
    }
}
