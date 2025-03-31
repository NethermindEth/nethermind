// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Blockchain;
using System.Collections.Generic;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Decoders;
using Nethermind.Core.Extensions;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetInclusionListTransactionsHandler(
    IBlockTree blockTree,
    TxPoolTxSource? txPoolTxSource)
    : IHandler<byte[][]>
{
    private readonly Random _rnd = new();

    public ResultWrapper<byte[][]> Handle()
    {
        if (txPoolTxSource is null)
        {
            return ResultWrapper<byte[][]>.Success([]);
        }

        // get highest priority fee transactions from txpool up to limit
        IEnumerable<Transaction> txs = txPoolTxSource.GetTransactions(blockTree.Head!.Header, long.MaxValue);
        var orderedTxs = OrderTransactions(txs);
        byte[][] txBytes = [.. DecodeTransactionsUpToLimit(orderedTxs)];
        return ResultWrapper<byte[][]>.Success(txBytes);
    }

    private IEnumerable<Transaction> OrderTransactions(IEnumerable<Transaction> txs)
        => txs.Shuffle(_rnd, Eip7805Constants.MaxTransactionsPerInclusionList);

    private static IEnumerable<byte[]> DecodeTransactionsUpToLimit(IEnumerable<Transaction> txs)
    {
        int size = 0;
        foreach (Transaction tx in txs)
        {
            byte[] txBytes = InclusionListDecoder.Encode(tx);

            // skip tx if it's too big to fit in the inclusion list
            if (size + txBytes.Length > Eip7805Constants.MaxBytesPerInclusionList)
            {
                continue;
            }

            size += txBytes.Length;
            yield return txBytes;

            // impossible to fit another tx in the inclusion list
            if (size + Eip7805Constants.MinTransactionSizeBytesUpper > Eip7805Constants.MaxBytesPerInclusionList)
            {
                yield break;
            }
        }
    }
}
