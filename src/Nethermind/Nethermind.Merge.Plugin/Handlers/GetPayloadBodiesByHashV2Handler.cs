// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using static Nethermind.Merge.Plugin.Handlers.PayloadBodiesHandlerHelper;

namespace Nethermind.Merge.Plugin.Handlers;

public sealed class GetPayloadBodiesByHashV2Handler(IHeaderStore headerStore, ILogManager logManager, IBlockAccessListStore balStore, IBlockStore blockStore)
    : IHandler<IReadOnlyList<Hash256>, IReadOnlyList<ExecutionPayloadBodyV2Result?>>
{
    private readonly ILogger _logger = logManager.GetClassLogger(typeof(GetPayloadBodiesByHashV2Handler));

    public ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>> Handle(IReadOnlyList<Hash256> blockHashes)
    {
        if (blockHashes.Count > PayloadBodiesHandlerHelper.MaxCount)
        {
            string error = $"The number of requested bodies must not exceed {PayloadBodiesHandlerHelper.MaxCount}";
            if (_logger.IsError) _logger.Error($"{GetType().Name}: {error}");
            return ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>.Fail(error, MergeErrorCodes.TooLargeRequest);
        }

        using ArrayPoolList<StoredBlock?> blocks = ReadBlocks(headerStore, blockStore, blockHashes);
        return ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV2Result?>>.Success(new PayloadBodiesV2DirectResponse(CreatePayloadBodiesV2(balStore, blocks)));
    }
}
