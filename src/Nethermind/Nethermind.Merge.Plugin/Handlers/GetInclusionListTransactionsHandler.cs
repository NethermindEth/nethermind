// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.JsonRpc;
using System.Collections.Generic;
using Nethermind.Consensus.Decoders;
using Nethermind.Core.Extensions;
using Nethermind.Core.Collections;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class GetInclusionListTransactionsHandler(ITxPool? txPool)
    : IHandler<ArrayPoolList<byte[]>>
{
    private readonly Random _rnd = new();

    public ResultWrapper<ArrayPoolList<byte[]>> Handle()
    {
        if (txPool is null)
        {
            return ResultWrapper<ArrayPoolList<byte[]>>.Success(ArrayPoolList<byte[]>.Empty());
        }

        // get highest priority fee transactions from txpool up to limit
        Transaction[] txs = txPool.GetPendingTransactions();
        var orderedTxs = OrderTransactions(txs);
        ArrayPoolList<byte[]> txBytes = new(Eip7805Constants.MaxTransactionsPerInclusionList, DecodeTransactionsUpToLimit(orderedTxs));
        return ResultWrapper<ArrayPoolList<byte[]>>.Success(txBytes);
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
