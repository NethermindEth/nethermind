// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain;
using Nethermind.Consensus.Decoders;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

internal class InclusionListBuilder(ITxPool txPool, IBlockTree blockTree, ISpecProvider specProvider, IReadOnlyStateProvider headState)
{
    // Conservative lower bound for an encoded transaction's size.
    private const int MinTransactionSizeBytes = 32;
    // Senders drawn per list. The byte cap, not this, decides how many of them reach the wire.
    private const int SenderSampleCapacity = Eip7805Constants.MaxBytesPerInclusionList / MinTransactionSizeBytes;

    /// <summary>Draws pending transactions for an inclusion list, up to the per-list byte cap.</summary>
    /// <param name="parent">Header the next-block base fee is derived from; the head when null.</param>
    /// <returns>The encoded transactions; the caller owns and disposes them.</returns>
    public InclusionListBytes GetInclusionList(BlockHeader? parent = null)
    {
        using ArrayPoolListRef<Transaction> sample = SampleAppendableTxs(parent);
        return EncodeTransactionsUpToLimit(in sample);
    }

    /// <summary>Draws candidate transactions for the list, round-robin across the drawn senders.</summary>
    /// <remarks>Restricted to each sender's appendable run, since nothing else could be appended. Drawn
    /// uniformly, not by fee: a fee-ordered draw drops what a builder passes over.</remarks>
    private ArrayPoolListRef<Transaction> SampleAppendableTxs(BlockHeader? parent)
    {
        const int capacity = SenderSampleCapacity;
        Random rnd = Random.Shared;
        UInt256 baseFee = NextBlockBaseFee(parent);

        // Reservoir over senders rather than over their runs, so the pool's size costs no allocation and no
        // state read: only the drawn senders below pay for one.
        using ArrayPoolListRef<Transaction[]> drawn = new(capacity);
        int seen = 0;
        // Blob txs cannot appear here: TxPool routes them to a separate pool this snapshot does not read. Frame
        // txs still can, inside a mixed bucket; only buckets holding nothing else are left out.
        foreach (Transaction[] pending in txPool.GetPendingTransactionsBySenderWithReadyNonFrameTx(baseFee).Values)
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

    /// <summary>The transactions of <paramref name="pending"/> the next block could append, in order.</summary>
    /// <remarks>The pool vouches only that some bucket entry is ready, so the run is rebuilt here against the
    /// account: frame transactions removed, spent nonces skipped, anchored at the account's next nonce, cut at the
    /// first nonce gap or unpayable base fee.</remarks>
    private Transaction[] AppendableRun(Transaction[] pending, in UInt256 baseFee)
    {
        Transaction[] bySender = WithoutFrameTxs(pending);
        if (bySender.Length == 0) return [];

        ulong anchor = headState.GetNonce(bySender[0].SenderAddress!);
        int start = 0;
        // The pool reads an entry under the account nonce as spent rather than blocking, so one can head the
        // bucket while a later entry is what got it admitted.
        while (start < bySender.Length && bySender[start].Nonce < anchor) start++;
        if (start == bySender.Length || bySender[start].Nonce != anchor) return [];

        int length = 0;
        // Buckets are nonce-ordered, so a broken offset can never realign: nothing behind a gap is appendable,
        // and nothing behind an entry the next block would price out is worth the byte cap either.
        while (start + length < bySender.Length
            && bySender[start + length].Nonce == anchor + (ulong)length
            && bySender[start + length].CanPayBaseFee(baseFee))
        {
            length++;
        }

        // The loop bounds length by bySender.Length - start, so a full-length run had nothing skipped and
        // start is 0: the whole array is the run and needs no copy.
        return length == bySender.Length ? bySender : bySender[start..(start + length)];
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

    /// <summary>The base fee the next block will charge.</summary>
    /// <remarks>Approximate at a fork boundary: the next timestamp is not derivable here, so the parent's
    /// stands in and pre-fork EIP-1559 parameters are resolved for a post-fork block.</remarks>
    private UInt256 NextBlockBaseFee(BlockHeader? parent)
    {
        parent ??= blockTree.Head?.Header;
        return parent is null ? UInt256.Zero : BaseFeeCalculator.Calculate(parent, specProvider.GetSpec(parent.Number + 1, parent.Timestamp));
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
