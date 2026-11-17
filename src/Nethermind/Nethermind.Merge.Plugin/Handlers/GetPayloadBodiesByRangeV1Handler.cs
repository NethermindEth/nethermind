// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

public sealed class GetPayloadBodiesByRangeV1Handler(IBlockTree blockTree, IBlockStore blockStore, ILogManager logManager)
    : IGetPayloadBodiesByRangeV1Handler
{
    private readonly ILogger _logger = logManager.GetClassLogger(typeof(GetPayloadBodiesByRangeV1Handler));

    public Task<ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>> Handle(ulong start, ulong count)
    {
        if (start < 1 || count < 1)
        {
            const string error = $"'{nameof(start)}' and '{nameof(count)}' must be positive numbers";
            if (_logger.IsError) _logger.Error($"{GetType().Name}: {error}");
            return ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>.Fail(error, ErrorCodes.InvalidParams);
        }

        if (count > PayloadBodiesHandlerHelper.MaxCount)
        {
            string error = $"The number of requested bodies must not exceed {PayloadBodiesHandlerHelper.MaxCount}";
            if (_logger.IsError) _logger.Error($"{GetType().Name}: {error}");
            return ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        return ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>.Success(GetRequests(start, count));
    }

    private PayloadBodiesV1DirectResponse GetRequests(ulong start, ulong count)
    {
        ulong headNumber = blockTree.Head?.Number ?? 0UL;
        ulong end = Math.Min(start + count - 1, headNumber);
        if (end < start) return new PayloadBodiesV1DirectResponse(Array.Empty<PayloadBodiesV1DirectResponse.PayloadBody?>());

        BlockHeader?[] headers = PayloadBodiesHandlerHelper.FindHeaders(blockTree, start, end);
        PayloadBodiesV1DirectResponse.PayloadBody?[] results = new PayloadBodiesV1DirectResponse.PayloadBody?[headers.Length];
        for (int i = 0; i < headers.Length; i++)
        {
            PayloadBodiesHandlerHelper.PrefetchBodies(blockStore, headers, i);
            results[i] = PayloadBodiesHandlerHelper.CreatePayloadBodyV1(blockStore, headers[i]);
        }

        return new PayloadBodiesV1DirectResponse(results);
    }
}
