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
using Nethermind.State.Repositories;
using static Nethermind.Merge.Plugin.Handlers.PayloadBodiesHandlerHelper;

namespace Nethermind.Merge.Plugin.Handlers;

public sealed class GetPayloadBodiesByRangeV2Handler(
    IBlockTree blockTree, IChainLevelInfoRepository chainLevels, ILogManager logManager, IBlockAccessListStore balStore, IBlockStore blockStore)
    : IGetPayloadBodiesByRangeV2Handler
{
    private readonly ILogger _logger = logManager.GetClassLogger(typeof(GetPayloadBodiesByRangeV2Handler));

    public Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>> Handle(ulong start, ulong count)
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

    private PayloadBodiesV2DirectResponse GetRequests(ulong start, ulong count)
    {
        ulong headNumber = blockTree.Head?.Number ?? 0;
        ulong end = Math.Min(start + count - 1, headNumber);
        if (end < start) return new PayloadBodiesV2DirectResponse(Array.Empty<PayloadBodiesV2DirectResponse.PayloadBody?>());

        return CreateResponseV2(balStore, ReadCanonicalBlocks(chainLevels, blockStore, start, end));
    }
}
