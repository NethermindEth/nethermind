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
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.handlers;

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
            }

            ExecutionPayloadBodyV2Result result = new(block!.Transactions, block.Withdrawals, null, null);

            ConsensusRequest[]? consensusRequests = block?.Requests;

            (int depositCount, int withdrawalRequestCount) = block != null ? block.Requests.GetTypeCounts() : (0, 0);

            result.DepositRequests = new Deposit[depositCount];
            result.WithdrawalRequests = new WithdrawalRequest[withdrawalRequestCount];

            int depositIndex = 0;
            int withdrawalRequestIndex = 0;

            if (consensusRequests is not null)
            {
                foreach (ConsensusRequest request in consensusRequests)
                {
                    if (request.Type == ConsensusRequestsType.Deposit)
                    {
                        result.DepositRequests![depositIndex++] = (Deposit)request;
                    }
                    else if (request.Type == ConsensusRequestsType.WithdrawalRequest)
                    {
                        result.WithdrawalRequests![withdrawalRequestIndex++] = (WithdrawalRequest)request;
                    }
                    else
                    {
                        var error = $"Unknown request type {request.Type}";
                        if (_logger.IsError) _logger.Error($"{nameof(GetPayloadBodiesByHashV2Handler)}: {error}");
                    }
                }
            }
            yield return result;
        }

        yield break;
    }
}
