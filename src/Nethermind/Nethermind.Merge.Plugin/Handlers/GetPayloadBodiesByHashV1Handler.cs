// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

public sealed class GetPayloadBodiesByHashV1Handler(IBlockTree blockTree, IBlockStore blockStore, ILogManager logManager)
    : IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV1Result?>>
{
    private readonly ILogger _logger = logManager.GetClassLogger(typeof(GetPayloadBodiesByHashV1Handler));

    public ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>> Handle(IReadOnlyList<Hash256> blockHashes)
    {
        if (blockHashes.Count > PayloadBodiesHandlerHelper.MaxCount)
        {
            string error = $"The number of requested bodies must not exceed {PayloadBodiesHandlerHelper.MaxCount}";
            if (_logger.IsError) _logger.Error($"{GetType().Name}: {error}");
            return ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        PayloadBodiesV1DirectResponse.PayloadBody?[] results = new PayloadBodiesV1DirectResponse.PayloadBody?[blockHashes.Count];
        for (int i = 0; i < blockHashes.Count; i++)
        {
            Hash256 blockHash = blockHashes[i];
            results[i] = PayloadBodiesHandlerHelper.CreatePayloadBodyV1(
                blockStore,
                blockTree.FindHeader(blockHash, PayloadBodiesHandlerHelper.HashLookupOptions),
                blockHash);
        }

        return ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>.Success(new PayloadBodiesV1DirectResponse(results));
    }
}
