// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Blocks;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

public sealed class GetPayloadBodiesByRangeV2Handler(IBlockTree blockTree, ILogManager logManager, IBlockAccessListStore balStore, IBlockStore blockStore)
    : IGetPayloadBodiesByRangeV2Handler
{
    private readonly ILogger _logger = logManager.GetClassLogger(typeof(GetPayloadBodiesByRangeV2Handler));

    public Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> Handle(long start, long count)
    {
        if (start < 1 || count < 1)
        {
            const string error = $"'{nameof(start)}' and '{nameof(count)}' must be positive numbers";
            if (_logger.IsError) _logger.Error($"{GetType().Name}: {error}");
            return ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>.Fail(error, ErrorCodes.InvalidParams);
        }

        if (count > PayloadBodiesHandlerHelper.MaxCount)
        {
            string error = $"The number of requested bodies must not exceed {PayloadBodiesHandlerHelper.MaxCount}";
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
                results[i - start] = PayloadBodiesHandlerHelper.CreatePayloadBodyV2(
                    blockStore,
                    balStore,
                    blockTree.FindHeader(i, PayloadBodiesHandlerHelper.RangeLookupOptions));
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
