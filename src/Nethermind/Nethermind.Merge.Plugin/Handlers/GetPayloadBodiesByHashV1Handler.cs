// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetPayloadBodiesByHashV1Handler(IBlockTree blockTree, ILogManager logManager)
    : IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV1Result?>>
{
    private const int MaxCount = 1024;
    private readonly ILogger _logger = logManager.GetClassLogger();

    protected bool CheckHashCount(IReadOnlyList<Hash256> blockHashes, out string? error)
    {
        if (blockHashes.Count > MaxCount)
        {
            error = $"The number of requested bodies must not exceed {MaxCount}";

            if (_logger.IsError) _logger.Error($"{GetType().Name}: {error}");
            return false;
        }
        error = null;
        return true;
    }

    public ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>> Handle(IReadOnlyList<Hash256> blockHashes) =>
        !CheckHashCount(blockHashes, out string? error)
            ? ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>.Fail(error!, MergeErrorCodes.TooLargeRequest)
            : ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>.Success(GetRequests(blockHashes));

    private IEnumerable<ExecutionPayloadBodyV1Result?> GetRequests(IReadOnlyList<Hash256> blockHashes)
    {
        for (int i = 0; i < blockHashes.Count; i++)
        {
            Block? block = blockTree.FindBlock(blockHashes[i]);
            yield return block is null ? null : new ExecutionPayloadBodyV1Result(block.Transactions, block.Withdrawals);
        }
    }
}
