// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus.Decoders;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class InclusionListBuilder(ITxPool txPool)
{
    public InclusionListBytes GetInclusionList()
    {
        using ArrayPoolListRef<Transaction> reservoir = ReservoirSampleNonBlobTxs(txPool.GetPendingTransactions());
        return EncodeTransactionsUpToLimit(in reservoir);
    }

    // Reservoir sample (Algorithm R + final Fisher-Yates) keeps memory at O(MaxTxs) for any mempool size.
    private static ArrayPoolListRef<Transaction> ReservoirSampleNonBlobTxs(Transaction[] mempool)
    {
        const int capacity = Eip7805Constants.MaxTransactionsPerInclusionList;
        ArrayPoolListRef<Transaction> reservoir = new(capacity);
        Random rnd = Random.Shared;
        int seen = 0;

        for (int i = 0; i < mempool.Length; i++)
        {
            Transaction tx = mempool[i];
            // Blob txs MUST NOT appear in an IL.
            if (tx.SupportsBlobs) continue;

            if (reservoir.Count < capacity)
            {
                reservoir.Add(tx);
            }
            else
            {
                int j = rnd.Next(seen + 1);
                if (j < capacity) reservoir[j] = tx;
            }
            seen++;
        }

        // The byte-cap loop below treats position as priority, so shuffle: membership alone isn't enough.
        for (int i = reservoir.Count - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (reservoir[i], reservoir[j]) = (reservoir[j], reservoir[i]);
        }

        return reservoir;
    }

    private static InclusionListBytes EncodeTransactionsUpToLimit(in ArrayPoolListRef<Transaction> txs)
    {
        InclusionListBytes result = new(txs.Count);
        try
        {
            int size = 0;
            foreach (Transaction tx in txs)
            {
                ArrayPoolList<byte> txBytes = InclusionListDecoder.EncodePooled(tx);

                if (size + txBytes.Count > Eip7805Constants.MaxBytesPerInclusionList)
                {
                    txBytes.Dispose();
                    continue;
                }

                size += txBytes.Count;
                result.Add(txBytes);

                // No possible tx can fit in the remaining space.
                if (size + Eip7805Constants.MinTransactionSizeBytes > Eip7805Constants.MaxBytesPerInclusionList)
                {
                    break;
                }
            }
            return result;
        }
        catch
        {
            // The caller only disposes result on the normal return, so a mid-loop throw would leak.
            result.Dispose();
            throw;
        }
    }
}
