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
         if (start < 1 || count < 1)
        {
            var error = $"'{nameof(start)}' and '{nameof(count)}' must be positive numbers";

            if (_logger.IsError) _logger.Error($"{nameof(GetPayloadBodiesByRangeV2Handler)}: ${error}");

            return ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>.Fail(error, ErrorCodes.InvalidParams);
        }
        if (count > MaxCount)
        {
            var error = $"The number of requested bodies must not exceed {MaxCount}";

            if (_logger.IsError) _logger.Error($"{nameof(GetPayloadBodiesByRangeV2Handler)}: {error}");

            return ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        return Task.FromResult(ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>.Success(GetRequests(start, count)));
    }


    private IEnumerable<ExecutionPayloadBodyV2Result?> GetRequests(long start, long count)
    {
        var headNumber = _blockTree.Head?.Number ?? 0;
        for (long i = start, c = Math.Min(start + count - 1, headNumber); i <= c; i++)
        {
            Block? block = _blockTree.FindBlock(start + i);

            ConsensusRequest[]? consensusRequests = block?.Requests;

            Deposit[]? deposits = Array.Empty<Deposit>();
            WithdrawalRequest[]? withdrawalRequests = Array.Empty<WithdrawalRequest>();

            if (consensusRequests is not null)
            {
                foreach (ConsensusRequest request in consensusRequests)
                {
                    if (request.Type == ConsensusRequestsType.Deposit)
                    {
                        deposits = (Deposit[])deposits.Append(request as Deposit);
                    }
                    else if (request.Type == ConsensusRequestsType.WithdrawalRequest)
                    {
                        withdrawalRequests = (WithdrawalRequest[])withdrawalRequests.Append(request as WithdrawalRequest);
                    }
                    else
                    {
                        var error = $"Unknown request type {request.Type}";
                        if (_logger.IsError) _logger.Error($"{nameof(GetPayloadBodiesByHashV2Handler)}: {error}");
                    }
                }
                yield return block is null ? null : new ExecutionPayloadBodyV2Result(block.Transactions, block.Withdrawals, deposits, withdrawalRequests);

            }

            yield return block is null ? null : new ExecutionPayloadBodyV2Result(block.Transactions, block.Withdrawals, null, null);
        }

        yield break;
    }
}
