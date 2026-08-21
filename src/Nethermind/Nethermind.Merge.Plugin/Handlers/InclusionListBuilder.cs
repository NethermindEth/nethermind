// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.Decoders;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class InclusionListBuilder(ITxPool txPool, IBlockTree blockTree, ISpecProvider specProvider)
{
    public InclusionListBytes GetInclusionList()
    {
        using ArrayPoolListRef<Transaction> sample = SampleAppendableTxs();
        return EncodeTransactionsUpToLimit(in sample);
    }

    /// <summary>Draws candidate transactions for the list, at most one sender's worth per draw.</summary>
    /// <remarks>
    /// Candidates are restricted to what the next block could actually append: senders whose lowest pending
    /// nonce is the account's next one and can pay the base fee, then that sender's gapless nonce run. An
    /// entry outside that set can never be appendable, so it would spend list bytes without adding
    /// censorship resistance. Senders are drawn uniformly rather than by fee, because the list exists for
    /// the transactions a builder passes over, which are exactly the ones a fee-ordered draw drops first.
    /// </remarks>
    private ArrayPoolListRef<Transaction> SampleAppendableTxs()
    {
        const int capacity = Eip7805Constants.MaxTransactionsPerInclusionList;
        Random rnd = Random.Shared;

        using ArrayPoolListRef<Transaction[]> senders = new(capacity);
        int seen = 0;
        foreach (Transaction[] bySender in txPool.GetPendingTransactionsBySender(filterToReadyTx: true, NextBlockBaseFee()).Values)
        {
            // Blob txs MUST NOT appear in an IL.
            if (bySender is not [{ SupportsBlobs: false }, ..]) continue;

            if (senders.Count < capacity)
            {
                senders.Add(bySender);
            }
            else
            {
                int j = rnd.Next(seen + 1);
                if (j < capacity) senders[j] = bySender;
            }
            seen++;
        }

        // The byte-cap loop below treats position as priority, so shuffle: membership alone isn't enough.
        for (int i = senders.Count - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (senders[i], senders[j]) = (senders[j], senders[i]);
        }

        ArrayPoolListRef<Transaction> sample = new(capacity);
        foreach (Transaction[] bySender in senders)
        {
            ulong nextNonce = bySender[0].Nonce;
            foreach (Transaction tx in bySender)
            {
                // A gap or a blob tx ends the run: nothing behind it can be appended either.
                if (sample.Count == capacity) return sample;
                if (tx.Nonce != nextNonce || tx.SupportsBlobs) break;

                sample.Add(tx);
                nextNonce++;
            }
        }
        return sample;
    }

    private UInt256 NextBlockBaseFee()
    {
        BlockHeader? head = blockTree.Head?.Header;
        return head is null ? UInt256.Zero : BaseFeeCalculator.Calculate(head, specProvider.GetSpec(head.Number + 1, head.Timestamp));
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
