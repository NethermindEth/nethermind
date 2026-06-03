// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Decoders;
using Nethermind.Core;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class InclusionListBuilder(ITxPool txPool)
{
    private readonly Random _rnd = new();

    public IEnumerable<byte[]> GetInclusionList() =>
        DecodeTransactionsUpToLimit(ReservoirSampleNonBlobTxs(txPool.GetPendingTransactions()));

    // Reservoir sample (Algorithm R + final Fisher-Yates) keeps memory at O(N=MaxTxs) for any
    // mempool size. The earlier .Where(...).Shuffle(...) path materialised the entire filtered
    // mempool into an ArrayPoolList that grew through power-of-2 reallocations.
    // TODO: score txs and randomly sample weighted by score.
    private Transaction[] ReservoirSampleNonBlobTxs(Transaction[] mempool)
    {
        const int capacity = Eip7805Constants.MaxTransactionsPerInclusionList;
        Transaction[] reservoir = new Transaction[capacity];
        int seen = 0;

        for (int i = 0; i < mempool.Length; i++)
        {
            Transaction tx = mempool[i];
            // blob txs MUST NOT appear in the IL.
            if (tx.Type == TxType.Blob) continue;

            if (seen < capacity)
            {
                reservoir[seen] = tx;
            }
            else
            {
                int j = _rnd.Next(seen + 1);
                if (j < capacity) reservoir[j] = tx;
            }
            seen++;
        }

        int actual = Math.Min(seen, capacity);
        // Fisher-Yates over the reservoir prefix — the byte-cap loop below treats position as
        // priority, so the order needs to be random too, not just the membership.
        for (int i = actual - 1; i > 0; i--)
        {
            int j = _rnd.Next(i + 1);
            (reservoir[i], reservoir[j]) = (reservoir[j], reservoir[i]);
        }

        if (actual < capacity) Array.Resize(ref reservoir, actual);
        return reservoir;
    }

    private static IEnumerable<byte[]> DecodeTransactionsUpToLimit(Transaction[] txs)
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
