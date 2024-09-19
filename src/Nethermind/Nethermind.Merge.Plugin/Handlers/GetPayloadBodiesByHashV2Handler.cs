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


public class GetPayloadBodiesByHashV2Handler(IBlockTree blockTree, ILogManager logManager)
    : GetPayloadBodiesByHashV1Handler(blockTree, logManager), IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV2Result?>>
{
    private readonly IBlockTree _blockTree = blockTree;

    public new ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>> Handle(IReadOnlyList<Hash256> blockHashes) =>
        !CheckHashCount(blockHashes, out string? error)
            ? ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>.Fail(error!, MergeErrorCodes.TooLargeRequest)
            : ResultWrapper<IEnumerable<ExecutionPayloadBodyV2Result?>>.Success(GetRequests(blockHashes));

    private IEnumerable<ExecutionPayloadBodyV2Result?> GetRequests(IReadOnlyList<Hash256> blockHashes)
    {
        for (int i = 0; i < blockHashes.Count; i++)
        {
            Block? block = _blockTree.FindBlock(blockHashes[i]);
            (Deposit[]? deposits, WithdrawalRequest[]? withdrawalRequests) = block?.Requests?.SplitRequests() ?? (null, null);
            yield return block is null ? null : new ExecutionPayloadBodyV2Result(block.Transactions, block.Withdrawals, deposits, withdrawalRequests);
        }
    }
}
