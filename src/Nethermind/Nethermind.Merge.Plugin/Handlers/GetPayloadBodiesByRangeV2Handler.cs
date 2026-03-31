// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetPayloadBodiesByRangeV2Handler(IBlockTree blockTree, ILogManager logManager, IBlockAccessListStore balStore)
    : GetPayloadBodiesByRangeHandler<ExecutionPayloadBodyV2Result>(blockTree, logManager), IGetPayloadBodiesByRangeV2Handler
{
    protected override ExecutionPayloadBodyV2Result CreateResult(Block block) =>
        new(block.Transactions, block.Withdrawals, block.Hash is null ? null : balStore.GetRlp(block.Hash));
}
