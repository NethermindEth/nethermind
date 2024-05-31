// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;


public class GetPayloadBodiesByHashV2Handler(IBlockTree blockTree, ILogManager logManager) : GetPayloadBodiesByHashV1Handler(blockTree, logManager), IAsyncHandler<IList<Hash256>, IEnumerable<ExecutionPayloadBodyV2Result?>>
{
    public new Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>> HandleAsync(IList<Hash256> blockHashes)
    {
        if (blockHashes.Count > MaxCount)
        {
            var error = $"The number of requested bodies must not exceed {MaxCount}";

            if (_logger.IsError) _logger.Error($"{nameof(GetPayloadBodiesByHashV2Handler)}: {error}");

            return ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        return Task.FromResult(ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>.Success(GetRequests(blockHashes)));
    }

    private IEnumerable<ExecutionPayloadBodyV2Result?> GetRequests(IList<Hash256> blockHashes)
    {
        for (int i = 0; i < blockHashes.Count; i++)
        {
            Block? block = _blockTree.FindBlock(blockHashes[i]);

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
