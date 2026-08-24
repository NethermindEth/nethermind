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
    // Conservative lower bound for an encoded transaction's size.
    private const int MinTransactionSizeBytes = 32;
    // Senders drawn per list. The byte cap, not this, decides how many of them reach the wire.
    private const int SenderSampleCapacity = Eip7805Constants.MaxBytesPerInclusionList / MinTransactionSizeBytes;

    public InclusionListBytes GetInclusionList()
    {
        using ArrayPoolListRef<Transaction> sample = SampleAppendableTxs();
        return EncodeTransactionsUpToLimit(in sample);
    }

    /// <summary>Draws candidate transactions for the list, round-robin across the drawn senders.</summary>
    /// <remarks>
    /// Candidates are restricted to what the next block could actually append: senders whose lowest pending
    /// nonce is the account's next one and can pay the base fee, then that sender's gapless nonce run. An
    /// entry outside that set can never be appendable, so it would spend list bytes without adding
    /// censorship resistance. Senders are drawn uniformly rather than by fee, because the list exists for
    /// the transactions a builder passes over, which are exactly the ones a fee-ordered draw drops first,
    /// and their runs are interleaved so no single account can spend the list on itself.
    /// </remarks>
    private ArrayPoolListRef<Transaction> SampleAppendableTxs()
    {
        const int capacity = SenderSampleCapacity;
        Random rnd = Random.Shared;

        using ArrayPoolListRef<Transaction[]> senders = new(capacity);
        int seen = 0;
        // Blob txs cannot appear here: TxPool routes them to a separate pool this snapshot does not read.
        foreach (Transaction[] bySender in txPool.GetPendingTransactionsBySender(filterToReadyTx: true, NextBlockBaseFee()).Values)
        {
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

        // Take one nonce per sender per round rather than draining each run in turn, so a single account
        // with a long ready run cannot spend the byte cap before the other drawn senders are represented.
        ArrayPoolListRef<Transaction> sample = new(capacity);
        for (int round = 0; ; round++)
        {
            bool advanced = false;
            foreach (Transaction[] bySender in senders)
            {
                if (round >= bySender.Length) continue;
                Transaction tx = bySender[round];
                // Buckets are nonce-ordered, so once a gap breaks this offset it can never realign: the
                // run ends there, because nothing behind a gap can be appended either.
                if (tx.Nonce != bySender[0].Nonce + (ulong)round) continue;

                sample.Add(tx);
                advanced = true;
                if (sample.Count == capacity) return sample;
            }
            if (!advanced) return sample;
        }
    }

    /// <summary>The base fee the next block will charge.</summary>
    /// <remarks>Approximate at a fork boundary: the next timestamp is not derivable here, so the parent's
    /// stands in and pre-fork EIP-1559 parameters are resolved for a post-fork block.</remarks>
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
                if (size + MinTransactionSizeBytes > Eip7805Constants.MaxBytesPerInclusionList)
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
