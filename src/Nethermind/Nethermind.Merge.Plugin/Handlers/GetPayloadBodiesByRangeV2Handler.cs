// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetPayloadBodiesByRangeV2Handler(IBlockTree blockTree, ILogManager logManager) : GetPayloadBodiesByRangeV1Handler(blockTree, logManager), IGetPayloadBodiesByRangeV2Handler
{
    public new Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>> Handle(long start, long count)
    {
        if (!CheckRangeCount(start, count, out string? error, out int errorCode))
        {
            return ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>.Fail(error!, errorCode);
        }

        return Task.FromResult(ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>.Success(GetRequests(start, count)));
    }


    private IEnumerable<ExecutionPayloadBodyV2Result?> GetRequests(long start, long count)
    {
        var headNumber = _blockTree.Head?.Number ?? 0;
        for (long i = start, c = Math.Min(start + count - 1, headNumber); i <= c; i++)
        {
            Block? block = _blockTree.FindBlock(i);

            if (block is null)
            {
                yield return null;
                continue;
            }

            (Deposit[]? deposits, WithdrawalRequest[]? withdrawalRequests, ConsolidationRequest[]? consolidationRequests) = block!.Requests?.SplitRequests() ?? (null, null, null);
            yield return new ExecutionPayloadBodyV2Result(block.Transactions, block.Withdrawals, deposits, withdrawalRequests, consolidationRequests);
        }

        yield break;
    }
}
