// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetPayloadBodiesByRangeV2Handler(IBlockTree blockTree, ILogManager logManager, IBlockAccessListStore balStore)
    : IGetPayloadBodiesByRangeV2Handler
{
    private const int MaxCount = 1024;

    private readonly ILogger _logger = logManager.GetClassLogger(typeof(GetPayloadBodiesByRangeV2Handler));

    public Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> Handle(long start, long count)
    {
        if (start < 1 || count < 1)
        {
            const string error = $"'{nameof(start)}' and '{nameof(count)}' must be positive numbers";
            if (_logger.IsError) _logger.Error($"{GetType().Name}: ${error}");
            return ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>.Fail(error, ErrorCodes.InvalidParams);
        }

        if (count > MaxCount)
        {
            string error = $"The number of requested bodies must not exceed {MaxCount}";
            if (_logger.IsError) _logger.Error($"{GetType().Name}: {error}");
            return ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        return ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>.Success(GetRequests(start, count));
    }

    private PayloadBodiesV2DirectResponse GetRequests(long start, long count)
    {
        long headNumber = blockTree.Head?.Number ?? 0;
        long end = Math.Min(start + count - 1, headNumber);
        if (end < start) return new PayloadBodiesV2DirectResponse([]);

        PayloadBodiesV2DirectResponse.PayloadBody?[] results = new PayloadBodiesV2DirectResponse.PayloadBody?[end - start + 1];
        try
        {
            for (long i = start; i <= end; i++)
            {
                Block? block = blockTree.FindBlock(i);
                if (block is null)
                {
                    continue;
                }

                MemoryManager<byte>? blockAccessList = block.Hash is null ? null : balStore.GetRlp(block.Hash);
                results[i - start] = PayloadBodiesV2DirectResponse.CreatePayloadBody(block.Transactions, block.Withdrawals, blockAccessList);
            }
        }
        catch
        {
            PayloadBodiesV2DirectResponse.DisposeItems(results);
            throw;
        }

        return new PayloadBodiesV2DirectResponse(results);
    }
}
