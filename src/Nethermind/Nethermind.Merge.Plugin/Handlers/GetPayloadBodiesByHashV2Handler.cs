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


public class GetPayloadBodiesByHashV2Handler(IBlockTree blockTree, ILogManager logManager) : GetPayloadBodiesByHashV1Handler(blockTree, logManager), IAsyncHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV2Result?>>
{
    public new Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>> HandleAsync(IReadOnlyList<Hash256> blockHashes)
    {
        if (!CheckHashCount(blockHashes, out string? error))
        {
            return ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>.Fail(error!, MergeErrorCodes.TooLargeRequest);
        }

        return Task.FromResult(ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>.Success(GetRequests(blockHashes)));
    }

    private IEnumerable<ExecutionPayloadBodyV2Result?> GetRequests(IReadOnlyList<Hash256> blockHashes)
    {
        for (int i = 0; i < blockHashes.Count; i++)
        {
            Block? block = _blockTree.FindBlock(blockHashes[i]);

            if (block is null)
            {
                yield return null;
                continue;
            }

            ExecutionPayloadBodyV2Result result = new(block!.Transactions, block.Withdrawals, null, null, null);

            ConsensusRequest[]? consensusRequests = block?.Requests;

            if (consensusRequests is not null)
            {
                (int depositCount, int withdrawalRequestCount, int consolidationRequestCount) = consensusRequests.GetTypeCounts();

                result.DepositRequests = new Deposit[depositCount];
                result.WithdrawalRequests = new WithdrawalRequest[withdrawalRequestCount];
                result.ConsolidationRequests = new ConsolidationRequest[consolidationRequestCount];

                int depositIndex = 0;
                int withdrawalRequestIndex = 0;
                int consolidationRequestIndex = 0;

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
                    else if (request.Type == ConsensusRequestsType.ConsolidationRequest)
                    {
                        result.ConsolidationRequests![consolidationRequestIndex++] = (ConsolidationRequest)request;
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
