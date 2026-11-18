// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using static Nethermind.Merge.Plugin.Handlers.PayloadBodiesHandlerHelper;

namespace Nethermind.Merge.Plugin.Handlers;

public sealed class GetPayloadBodiesByHashV1Handler(IHeaderStore headerStore, IBlockStore blockStore, ILogManager logManager)
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

        return ResultWrapper<IReadOnlyList<ExecutionPayloadBodyV1Result?>>.Success(CreateResponseV1(ReadBlocks(headerStore, blockStore, blockHashes)));
    }
}
