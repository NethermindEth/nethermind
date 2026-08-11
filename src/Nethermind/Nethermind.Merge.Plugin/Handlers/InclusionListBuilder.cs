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
        using ArrayPoolList<Transaction> reservoir = ReservoirSampleNonBlobTxs(txPool.GetPendingTransactions());
        return EncodeTransactionsUpToLimit(reservoir);
    }

    // Reservoir sample (Algorithm R + final Fisher-Yates) keeps memory at O(MaxTxs) for any mempool size.
    // TODO: score txs and randomly sample weighted by score.
    private static ArrayPoolList<Transaction> ReservoirSampleNonBlobTxs(Transaction[] mempool)
    {
        const int capacity = Eip7805Constants.MaxTransactionsPerInclusionList;
        ArrayPoolList<Transaction> reservoir = new(capacity);
        Random rnd = Random.Shared;
        int seen = 0;

        for (int i = 0; i < mempool.Length; i++)
        {
            Transaction tx = mempool[i];
            // Blob txs MUST NOT appear in an IL.
            if (tx.Type == TxType.Blob) continue;

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
        int size = 0;
        // EIP-8369 includer budget-fill: Profile-1 txs pass through freely; Profile-2 frame txs are
        // metered against the per-IL VERIFY budget; anything outside enforcement is excluded.
        ulong verifyBudget = Eip8369Constants.MaxVerifyGasPerIl;
        foreach (Transaction tx in txs)
        {
            FocilProfile profile = Eip8369.Classify(tx);
            if (profile == FocilProfile.Outside) continue;

            // Cost is uncomputable / over-budget / doesn't fit → ignore the tx, consume nothing.
            ulong cost = 0;
            if (profile == FocilProfile.Two)
            {
                cost = Eip8369.Profile2VerifyCost(tx);
                if (cost > Eip8369Constants.MaxVerifyGasPerTx || cost > verifyBudget) continue;
            }

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
}
