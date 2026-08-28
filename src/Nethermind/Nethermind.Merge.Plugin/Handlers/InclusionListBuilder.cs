// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.Decoders;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

internal class InclusionListBuilder(ITxPool txPool, IBlockTree blockTree, ISpecProvider specProvider, IReadOnlyStateProvider headState)
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
    /// <remarks>Restricted to each sender's appendable run, since nothing else could be appended. Drawn
    /// uniformly, not by fee: a fee-ordered draw drops what a builder passes over.</remarks>
    private ArrayPoolListRef<Transaction> SampleAppendableTxs()
    {
        const int capacity = SenderSampleCapacity;
        Random rnd = Random.Shared;
        UInt256 baseFee = NextBlockBaseFee();

        // Reservoir over senders rather than over their runs, so the pool's size costs no allocation and no
        // state read: only the drawn senders below pay for one.
        using ArrayPoolListRef<Transaction[]> drawn = new(capacity);
        int seen = 0;
        // Blob txs cannot appear here: TxPool routes them to a separate pool this snapshot does not read.
        foreach (Transaction[] pending in txPool.GetPendingTransactionsBySender(filterToReadyTx: true, baseFee).Values)
        {
            if (drawn.Count < capacity)
            {
                drawn.Add(pending);
            }
            else
            {
                int j = rnd.Next(seen + 1);
                if (j < capacity) drawn[j] = pending;
            }
            seen++;
        }

        // The byte-cap loop below treats position as priority, so shuffle: membership alone isn't enough.
        for (int i = drawn.Count - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (drawn[i], drawn[j]) = (drawn[j], drawn[i]);
        }

        using ArrayPoolListRef<Transaction[]> runs = new(capacity);
        foreach (Transaction[] pending in drawn)
        {
            Transaction[] run = AppendableRun(pending, in baseFee);
            if (run.Length > 0) runs.Add(run);
        }

        // Take one nonce per sender per round rather than draining each run in turn, so a single account
        // with a long ready run cannot spend the byte cap before the other drawn senders are represented.
        ArrayPoolListRef<Transaction> sample = new(capacity);
        for (int round = 0; ; round++)
        {
            bool advanced = false;
            foreach (Transaction[] run in runs)
            {
                if (round >= run.Length) continue;

                sample.Add(run[round]);
                advanced = true;
                if (sample.Count == capacity) return sample;
            }
            if (!advanced) return sample;
        }
    }

    /// <summary>The leading transactions of <paramref name="pending"/> the next block could append, in order.</summary>
    /// <remarks>The pool vouches for its first entry alone, so the rest is re-checked here: frame transactions
    /// removed, anchored at the account's next nonce, cut at the first nonce gap or unpayable base fee.</remarks>
    private Transaction[] AppendableRun(Transaction[] pending, in UInt256 baseFee)
    {
        Transaction[] bySender = WithoutFrameTxs(pending);
        if (bySender.Length == 0 || !IsAnchoredAtNextAccountNonce(pending, bySender)) return [];

        ulong anchor = bySender[0].Nonce;
        int length = 0;
        // Buckets are nonce-ordered, so a broken offset can never realign: nothing behind a gap is appendable,
        // and nothing behind an entry the next block would price out is worth the byte cap either.
        while (length < bySender.Length
            && bySender[length].Nonce == anchor + (ulong)length
            && bySender[length].CanPayBaseFee(baseFee))
        {
            length++;
        }

        return length == bySender.Length ? bySender : bySender[..length];
    }

    /// <summary>The sender's pending run with its EIP-8141 frame transactions removed.</summary>
    /// <remarks>
    /// Listing one spends the byte cap without buying censorship resistance, and its EIP-8250 keyed nonce
    /// shares <see cref="Transaction.Nonce"/> while counting per key, breaking the offsets behind it.
    /// </remarks>
    private static Transaction[] WithoutFrameTxs(Transaction[] bySender)
    {
        int kept = 0;
        foreach (Transaction tx in bySender)
            if (!tx.SupportsFrames) kept++;
        if (kept == bySender.Length) return bySender;

        Transaction[] result = new Transaction[kept];
        int j = 0;
        foreach (Transaction tx in bySender)
            if (!tx.SupportsFrames) result[j++] = tx;
        return result;
    }

    /// <summary>Whether <paramref name="bySender"/> still begins at the sender's next account nonce.</summary>
    /// <remarks>
    /// The pool admits a whole bucket on <paramref name="pending"/>[0] being ready, so that entry alone names the
    /// next account nonce — unless it is an EIP-8250 keyed one, which names a per-key sequence and so needs a read.
    /// </remarks>
    private bool IsAnchoredAtNextAccountNonce(Transaction[] pending, Transaction[] bySender) =>
        KeyedNonceManager.UsesKeyedNonce(pending[0])
            ? bySender[0].Nonce == headState.GetNonce(bySender[0].SenderAddress!)
            : bySender[0].Nonce == pending[0].Nonce;

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
