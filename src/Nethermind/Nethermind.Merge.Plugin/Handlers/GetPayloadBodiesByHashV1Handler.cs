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
    : IHandler<IReadOnlyList<Hash256>, IReadOnlyList<TResult?>> where TResult : class
{
    protected readonly IBlockTree _blockTree = blockTree;
    private const int MaxCount = 1024;
    private readonly ILogger _logger = logManager.GetClassLogger(typeof(GetPayloadBodiesByHashHandler<>));

    public ResultWrapper<IReadOnlyList<TResult?>> Handle(IReadOnlyList<Hash256> blockHashes)
    {
        if (blockHashes.Count > MaxCount)
        {
            string error = $"The number of requested bodies must not exceed {MaxCount}";
            if (_logger.IsError) _logger.Error($"{GetType().Name}: {error}");
            return ResultWrapper<IReadOnlyList<TResult?>>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        TResult?[] results = new TResult?[blockHashes.Count];
        for (int i = 0; i < blockHashes.Count; i++)
        {
            Block? block = _blockTree.FindBlock(blockHashes[i]);
            results[i] = block is null ? null : CreateResult(block, blockHashes[i]);
        }
        return ResultWrapper<IReadOnlyList<TResult?>>.Success(results);
    }

    protected abstract TResult? CreateResult(Block block, Hash256 blockHash);
}

public class GetPayloadBodiesByHashV1Handler(IBlockTree blockTree, ILogManager logManager)
    : GetPayloadBodiesByHashHandler<ExecutionPayloadBodyV1Result>(blockTree, logManager)
{
    protected override ExecutionPayloadBodyV1Result CreateResult(Block block, Hash256 blockHash) =>
        new(block.Transactions, block.Withdrawals);
}
