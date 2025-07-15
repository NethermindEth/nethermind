// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Decoders;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class InclusionListBuilder(ITxPool txPool)
{
    private readonly Random _rnd = new();

    public IEnumerable<byte[]> GetInclusionList()
    {
        Transaction[] txs = txPool.GetPendingTransactions();
        IEnumerable<Transaction> orderedTxs = OrderTransactions(txs);
        return DecodeTransactionsUpToLimit(orderedTxs);
    }

    // todo: score txs and randomly sample weighted by score
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
