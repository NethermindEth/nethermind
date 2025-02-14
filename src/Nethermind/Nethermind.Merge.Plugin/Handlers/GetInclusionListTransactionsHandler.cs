// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Rlp;
using Nethermind.Blockchain;
using System.Collections.Generic;
using Nethermind.Consensus.Transactions;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetInclusionListTransactionsHandler(
    IBlockTree blockTree,
    ITxSource inclusionListTxSource)
    : IHandler<byte[][]>
{
    private const int MaxILSizeBytes = 8000;

    public ResultWrapper<byte[][]> Handle()
    {
        // todo: get top transactions from txpool or something else?
        IEnumerable<Transaction> txs = inclusionListTxSource.GetTransactions(blockTree.Head!.Header, long.MaxValue);
        byte[][] txBytes = [.. DecodeTransactionsUpToLimit(txs)];
        return ResultWrapper<byte[][]>.Success(txBytes);
    }

    private static IEnumerable<byte[]> DecodeTransactionsUpToLimit(IEnumerable<Transaction> txs)
    {
        int size = 0;
        foreach (Transaction tx in txs)
        {
            byte[] txBytes = Rlp.Encode(tx).Bytes;
            size += txBytes.Length;
            if (size > MaxILSizeBytes)
            {
                break;
            }
            yield return txBytes;
        }
    }
}
