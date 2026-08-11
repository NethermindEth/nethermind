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
        using ArrayPoolList<Transaction> reservoir = ReservoirSampleEnforceableTxs(txPool.GetPendingTransactions());
        return EncodeTransactionsUpToLimit(reservoir);
    }

    // Reservoir sample (Algorithm R + final Fisher-Yates) keeps memory at O(MaxTxs) for any mempool size.
    // TODO: score txs and randomly sample weighted by score.
    private static ArrayPoolList<Transaction> ReservoirSampleEnforceableTxs(Transaction[] mempool)
    {
        const int capacity = Eip7805Constants.MaxTransactionsPerInclusionList;
        ArrayPoolList<Transaction> reservoir = new(capacity);
        Random rnd = Random.Shared;
        int seen = 0;

        for (int i = 0; i < mempool.Length; i++)
        {
            Transaction tx = mempool[i];
            // Exclude here, not at encode time, so FOCIL-unenforceable txs (blobs, wrong-shape /
            // over-budget frame txs) don't consume reservoir slots and leave the IL under-filled.
            if (tx.Type == TxType.Blob || Eip8369.Classify(tx) == FocilProfile.Outside) continue;

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

        // Fisher-Yates over the reservoir — the byte-cap loop below treats position as
        // priority, so the order needs to be random too, not just the membership.
        for (int i = reservoir.Count - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (reservoir[i], reservoir[j]) = (reservoir[j], reservoir[i]);
        }

        return reservoir;
    }

    private static InclusionListBytes EncodeTransactionsUpToLimit(ArrayPoolList<Transaction> txs)
    {
        InclusionListBytes result = new(txs.Count);
        try
        {
            int size = 0;
            // EIP-8369 includer budget-fill: sampling already dropped Outside txs, so only Profile-1
            // (free) and Profile-2 (metered against the per-IL VERIFY budget) remain.
            ulong verifyBudget = Eip8369Constants.MaxVerifyGasPerIl;
            foreach (Transaction tx in txs)
            {
                // Profile-2 cost is <= MaxVerifyGasPerTx by classification, so only the remaining per-IL
                // budget can reject it; Profile-1 costs nothing.
                ulong cost = Eip8369.Classify(tx) == FocilProfile.Two ? Eip8369.Profile2VerifyCost(tx) : 0;
                if (cost > verifyBudget) continue;

                ArrayPoolList<byte> txBytes = InclusionListDecoder.EncodePooled(tx);

                if (size + txBytes.Count > Eip7805Constants.MaxBytesPerInclusionList)
                {
                    txBytes.Dispose();
                    continue;
                }

                size += txBytes.Count;
                result.Add(txBytes);
                // Charge the VERIFY budget only once the Profile-2 tx is actually admitted to the IL.
                verifyBudget -= cost;

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
            // Dispose the pooled buffers accumulated so far before propagating — the caller only
            // disposes result on the normal return, so a mid-loop throw would otherwise leak them.
            result.Dispose();
            throw;
        }
    }
}
