// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data.V1;

namespace Nethermind.Merge.Plugin.Handlers.V1;

public class GetPayloadBodiesByRangeV1Handler : IGetPayloadBodiesByRangeV1Handler
{
    private readonly IBlockTree _blockTree;
    private readonly ILogger _logger;

    private const long MaxPayloadBodies = 1024;

    public GetPayloadBodiesByRangeV1Handler(IBlockTree blockTree, ILogManager logManager)
    {
        _blockTree = blockTree;
        _logger = logManager.GetClassLogger();
    }

    public Task<ResultWrapper<ExecutionPayloadBodyV1Result?[]>> Handle(long start, long count)
    {
        if (count > MaxPayloadBodies)
        {
            if (_logger.IsInfo) _logger.Info($"{nameof(GetPayloadBodiesByRangeV1Handler)}. Too many payloads requested. Count: {count}");
            return ResultWrapper<ExecutionPayloadBodyV1Result?[]>.Fail("Too many payloads requested",
                ErrorCodes.LimitExceeded);
        }

        var payloadBodies = new ExecutionPayloadBodyV1Result?[count];
        for (int i = 0; i < count; i++)
        {
            Block? block = _blockTree.FindBlock(start + i);
            payloadBodies[i] = block is not null ? new ExecutionPayloadBodyV1Result(block.Transactions) : null;
        }

        return ResultWrapper<ExecutionPayloadBodyV1Result?[]>.Success(payloadBodies);
    }
}
