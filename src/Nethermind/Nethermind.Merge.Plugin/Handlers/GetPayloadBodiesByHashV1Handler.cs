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

public abstract class GetPayloadBodiesByHashHandler<TResult>(IBlockTree blockTree, ILogManager logManager)
    : IHandler<IReadOnlyList<Hash256>, IEnumerable<TResult?>> where TResult : class
{
    protected readonly IBlockTree _blockTree = blockTree;
    private const int MaxCount = 1024;
    private readonly ILogger _logger = logManager.GetClassLogger(typeof(GetPayloadBodiesByHashHandler<>));

    public ResultWrapper<IEnumerable<TResult?>> Handle(IReadOnlyList<Hash256> blockHashes)
    {
        if (blockHashes.Count > MaxCount)
        {
            string error = $"The number of requested bodies must not exceed {MaxCount}";
            if (_logger.IsError) _logger.Error($"{GetType().Name}: {error}");
            return ResultWrapper<IEnumerable<TResult?>>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        return ResultWrapper<IEnumerable<TResult?>>.Success(GetRequests(blockHashes));
    }

    protected abstract TResult? CreateResult(Block block, Hash256 blockHash);

    private IEnumerable<TResult?> GetRequests(IReadOnlyList<Hash256> blockHashes)
    {
        for (int i = 0; i < blockHashes.Count; i++)
        {
            Block? block = _blockTree.FindBlock(blockHashes[i]);
            yield return block is null ? null : CreateResult(block, blockHashes[i]);
        }
    }
}

public class GetPayloadBodiesByHashV1Handler(IBlockTree blockTree, ILogManager logManager)
    : GetPayloadBodiesByHashHandler<ExecutionPayloadBodyV1Result>(blockTree, logManager)
{
    protected override ExecutionPayloadBodyV1Result CreateResult(Block block, Hash256 blockHash) =>
        new(block.Transactions, block.Withdrawals);
}
