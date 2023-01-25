// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    private readonly IBlockTree _blockTree;
    private readonly ILogger _logger;

    private const long MaxPayloadBodies = 1024;

    public GetPayloadBodiesByRangeV1Handler(IBlockTree blockTree, ILogManager logManager)
    {
        _blockTree = blockTree;
        _logger = logManager.GetClassLogger();
    }

    public Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>> Handle(long start, long count)
    {
        if (count > MaxPayloadBodies)
        {
            if (_logger.IsInfo) _logger.Info($"{nameof(GetPayloadBodiesByRangeV1Handler)}. Too many payloads requested. Count: {count}");
            return ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>.Fail("Too many payloads requested",
                ErrorCodes.LimitExceeded);
        }

        var payloadBodies = new ExecutionPayloadBodyV1Result?[count];
        var skipFrom = 0;

        for (int i = 0; i < count; i++)
        {
            var block = _blockTree.FindBlock(start + i);

            if (block is null)
            {
                payloadBodies[i] = null;

                if (skipFrom == 0 && i > 0 && payloadBodies[i - 1] is not null)
                    skipFrom = i;
            }
            else
            {
                payloadBodies[i] = new(block.Transactions, block.Withdrawals);
                skipFrom = 0;
            }
        }

        var trimmedBodies = skipFrom > 0 ? payloadBodies.SkipLast(payloadBodies.Length - skipFrom) : payloadBodies;

        return ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>.Success(trimmedBodies);
    }
}
