// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

public sealed class GetPayloadBodiesByHashV2Handler(IBlockTree blockTree, ILogManager logManager, IBlockAccessListStore balStore, IBlockStore blockStore)
    : IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV2Result?>>
{
    private const int MaxCount = 1024;
    private const BlockTreeLookupOptions LookupOptions =
        BlockTreeLookupOptions.TotalDifficultyNotNeeded |
        BlockTreeLookupOptions.DoNotCreateLevelIfMissing;

    private readonly ILogger _logger = logManager.GetClassLogger(typeof(GetPayloadBodiesByHashV2Handler));

    public ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>> Handle(IReadOnlyList<Hash256> blockHashes)
    {
        if (blockHashes.Count > MaxCount)
        {
            string error = $"The number of requested bodies must not exceed {MaxCount}";
            if (_logger.IsError) _logger.Error($"{GetType().Name}: {error}");
            return ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        PayloadBodiesV2DirectResponse.PayloadBody?[] results = new PayloadBodiesV2DirectResponse.PayloadBody?[blockHashes.Count];
        try
        {
            for (int i = 0; i < blockHashes.Count; i++)
            {
                Hash256 blockHash = blockHashes[i];
                BlockHeader? header = blockTree.FindHeader(blockHash, LookupOptions);
                if (header is null)
                {
                    continue;
                }

                byte[]? blockRlp = blockStore.GetRlp(header.Number, blockHash);
                if (blockRlp is null)
                {
                    continue;
                }

                MemoryManager<byte>? blockAccessList = balStore.GetRlp(header.Number, blockHash);
                results[i] = PayloadBodiesV2DirectResponse.CreatePayloadBody(blockRlp, blockAccessList);
            }
        }
        catch
        {
            PayloadBodiesV2DirectResponse.DisposeItems(results);
            throw;
        }

        return ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>.Success(new PayloadBodiesV2DirectResponse(results));
    }
}
