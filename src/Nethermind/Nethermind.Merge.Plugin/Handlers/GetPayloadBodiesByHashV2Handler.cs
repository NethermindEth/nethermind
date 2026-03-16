// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetPayloadBodiesByHashV2Handler(IBlockTree blockTree, ILogManager logManager, IBlockAccessListStore balStore)
    : GetPayloadBodiesByHashHandler<ExecutionPayloadBodyV2Result>(blockTree, logManager)
{
    protected override ExecutionPayloadBodyV2Result CreateResult(Block block, Hash256 blockHash) =>
        new(block.Transactions, block.Withdrawals, balStore.GetRlp(blockHash));
}
