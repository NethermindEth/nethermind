// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Rlp;
using System.Linq;
using Nethermind.Consensus.Producers;
using Nethermind.Blockchain;
using System.Collections.Generic;
using Nethermind.Consensus.Transactions;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetInclusionListTransactionsHandler(
    IBlockTree blockTree,
    ITxSource inclusionListTxSource)
    : IHandler<byte[][]>
{
    public ResultWrapper<byte[][]> Handle()
    {
        // todo: limit size of IL?
        IEnumerable<Transaction> txs = inclusionListTxSource.GetTransactions(blockTree.Head!.Header, long.MaxValue);
        byte[][] txBytes = [.. txs.Select(tx => Rlp.Encode(tx).Bytes)];
        return ResultWrapper<byte[][]>.Success(txBytes);
    }
}