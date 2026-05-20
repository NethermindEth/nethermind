// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
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
    protected override ExecutionPayloadBodyV2Result CreateResult(Block block, Hash256 blockHash)
    {
        using MemoryManager<byte>? blockAccessList = balStore.GetRlp(blockHash);
        return new(block.Transactions, block.Withdrawals, blockAccessList?.Memory.ToArray());
    }
}
