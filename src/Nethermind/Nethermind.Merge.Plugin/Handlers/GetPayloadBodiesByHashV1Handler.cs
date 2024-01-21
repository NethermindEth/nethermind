// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetPayloadBodiesByHashV1Handler : IAsyncHandler<IList<Hash256>, IEnumerable<ExecutionPayloadBodyV1Result?>>
{
    private const int MaxCount = 1024;
    private readonly IBlockTree _blockTree;
    private readonly Logger _logger;

    public GetPayloadBodiesByHashV1Handler(IBlockTree blockTree, ILogManager logManager)
    {
        _blockTree = blockTree;
        _logger = logManager.GetClassLogger();
    }

    public Task<ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>> HandleAsync(IList<Hash256> blockHashes)
    {
        if (blockHashes.Count > MaxCount)
        {
            var error = $"The number of requested bodies must not exceed {MaxCount}";

            if (_logger.IsError) _logger.Error($"{nameof(GetPayloadBodiesByHashV1Handler)}: {error}");

            return ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        return Task.FromResult(ResultWrapper<IEnumerable<ExecutionPayloadBodyV1Result?>>.Success(GetRequests(blockHashes)));
    }

    private IEnumerable<ExecutionPayloadBodyV1Result?> GetRequests(IList<Hash256> blockHashes)
    {
        for (int i = 0; i < blockHashes.Count; i++)
        {
            Block? block = _blockTree.FindBlock(blockHashes[i]);

            yield return (block is null ? null : new ExecutionPayloadBodyV1Result(block.Transactions, block.Withdrawals));
        }

        yield break;
    }
}
