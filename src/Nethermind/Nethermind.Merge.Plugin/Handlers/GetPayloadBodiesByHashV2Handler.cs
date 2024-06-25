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

            (Deposit[]? deposits, WithdrawalRequest[]? withdrawalRequests) = block!.Requests.SplitRequests();
            yield return new ExecutionPayloadBodyV2Result(block.Transactions, block.Withdrawals, deposits, withdrawalRequests);
        }

        yield break;
    }
}
