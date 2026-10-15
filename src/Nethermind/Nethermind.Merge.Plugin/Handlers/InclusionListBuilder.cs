// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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

/// <param name="maxVerifyGasPerTx">EIP-7805 <c>MAX_VERIFY_GAS_PER_TX</c>, above which a frame transaction is no
/// Profile 2 candidate; <c>0</c> lifts the cap.</param>
internal class InclusionListBuilder(ITxPool txPool, IBlockTree blockTree, ISpecProvider specProvider, IReadOnlyStateProvider headState,
    ulong maxVerifyGasPerTx = Eip8369Constants.MaxVerifyGasPerTx)
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
    /// uniformly, not by fee: a fee-ordered draw drops what a builder passes over. Once EIP-7805's Profile 2 is
    /// active, each ready Profile 2 candidate frame transaction is drawn as a run of its own beside the senders.
    /// Not gated on pool revalidation: the ready-tx snapshot applies no per-transaction spec check, so around a
    /// fork the draw can include entries the new spec rejects — either because the pool's background revalidation
    /// is still catching up, or because the target block is the activation slot itself, whose timestamp is
    /// not known from the parent header. The only cost is wasted list bytes.</remarks>
    private ArrayPoolListRef<Transaction> SampleAppendableTxs(BlockHeader? parent)
    {
        const int capacity = SenderSampleCapacity;
        Random rnd = Random.Shared;
        parent ??= blockTree.Head?.Header;
        IReleaseSpec? spec = NextBlockSpec(parent);
        UInt256 baseFee = parent is null ? UInt256.Zero : BaseFeeCalculator.Calculate(parent, spec!);

        // Reservoir over senders rather than over their runs, so the pool's size costs no allocation and no
        // state read: only the drawn senders below pay for one.
        using ArrayPoolListRef<Draw> drawn = new(capacity);
        int seen = 0;
        foreach (Draw draw in Draws(parent, spec, baseFee))
        {
            if (drawn.Count < capacity)
            {
                drawn.Add(draw);
            }
            else
            {
                int j = rnd.Next(seen + 1);
                if (j < capacity) drawn[j] = draw;
            }
            seen++;
        }

        // The byte-cap loop below treats position as priority, so shuffle: membership alone isn't enough.
        for (int i = drawn.Count - 1; i > 0; i--)
        {
            int j = rnd.Next(i + 1);
            (drawn[i], drawn[j]) = (drawn[j], drawn[i]);
        }

        // Runs are held as ranges: one over the sender's own bucket where nothing had to be filtered out,
        // and otherwise over a single shared buffer the filtered entries are appended to.
        ArrayPoolListRef<Transaction> filtered = new(capacity);
        try
        {
            using ArrayPoolListRef<Run> runs = new(capacity);
            foreach (Draw draw in drawn)
            {
                Run run = draw.FrameCandidate is { } candidate
                    ? ReadyCandidateRun(candidate, ref filtered)
                    : AppendableRun(draw.Bucket!, in baseFee, ref filtered);
                if (run.Length > 0) runs.Add(run);
            }

            // Take one nonce per sender per round rather than draining each run in turn, so a single account
            // with a long ready run cannot spend the byte cap before the other drawn senders are represented.
            ArrayPoolListRef<Transaction> sample = new(capacity);
            for (int round = 0; ; round++)
            {
                bool advanced = false;
                foreach (Run run in runs)
                {
                    if (round >= run.Length) continue;

                    sample.Add(run.Bucket is null ? filtered[run.Start + round] : run.Bucket[run.Start + round]);
                    advanced = true;
                    if (sample.Count == capacity) return sample;
                }
                if (!advanced) return sample;
            }
        }
        finally
        {
            // A ref struct cannot be a using variable and a ref argument both.
            filtered.Dispose();
        }
    }

    /// <summary>A sender's appendable run, as a range over its bucket or over the shared filtered buffer.</summary>
    private readonly record struct Run(Transaction[]? Bucket, int Start, int Length);

    /// <summary>One reservoir entry: a sender's bucket, or a Profile 2 candidate frame transaction.</summary>
    private readonly record struct Draw(Transaction[]? Bucket, Transaction? FrameCandidate);

    /// <summary>What the reservoir draws from: the senders with a ready non-frame transaction, then, once Profile 2 is
    /// active, the pool's Profile 2 candidates.</summary>
    private IEnumerable<Draw> Draws(BlockHeader? parent, IReleaseSpec? spec, UInt256 baseFee)
    {
        // The pool routes blob txs to separate storage this snapshot never reads. Frame txs can remain in
        // mixed sender buckets and are removed below.
        foreach (Transaction[] pending in txPool.GetPendingTransactionsBySenderWithReadyNonFrameTx(baseFee).Values)
        {
            yield return new Draw(pending, null);
        }

        if (parent is null || spec is null || !Eip8369Profile2.IsActive(spec)) yield break;

        foreach (Transaction tx in txPool.GetPendingTransactions())
        {
            if (IsProfile2Candidate(tx, in baseFee, parent.Timestamp)) yield return new Draw(null, tx);
        }
    }

    /// <summary>Whether <paramref name="tx"/> is a Profile 2 candidate worth listing, judged without reading state.</summary>
    /// <remarks>EIP-7805 § IL Building: a member should leave out a candidate whose expiry deadline is already behind
    /// the target slot. The slot's timestamp is not known here, so only one at or behind the parent's is.</remarks>
    private bool IsProfile2Candidate(Transaction tx, in UInt256 baseFee, ulong parentTimestamp) =>
        tx.SupportsFrames
        && tx.SenderAddress is not null
        && tx.CanPayBaseFee(baseFee)
        && Eip8369Profile2.Classify(tx, maxVerifyGasPerTx) == Profile2Exclusion.None
        && Eip8369Profile2.VerifyBudgetCost(tx) <= Eip8369Constants.MaxVerifyGasPerIl
        && !(FrameTxValidation.TryGetExpiryDeadline(tx, out ulong deadline) && deadline <= parentTimestamp);

    /// <summary>A drawn Profile 2 candidate as a run of its own, empty unless its nonce is the next one at the head.</summary>
    /// <remarks>An EIP-8250 keyed transaction is judged on its own keys, an account-domain one on the account nonce.</remarks>
    private Run ReadyCandidateRun(Transaction candidate, ref ArrayPoolListRef<Transaction> filtered)
    {
        Address sender = candidate.SenderAddress!;
        bool ready = KeyedNonceManager.UsesKeyedNonce(candidate)
            ? KeyedNonceManager.IsNonceSetValid(headState, sender, candidate.NonceKeys!, candidate.Nonce)
            : headState.GetNonce(sender) == candidate.Nonce;
        if (!ready) return default;

        filtered.Add(candidate);
        return new Run(null, filtered.Count - 1, 1);
    }

    /// <summary>The transactions of <paramref name="pending"/> the next block could append, in order.</summary>
    /// <remarks>The pool vouches only that some bucket entry is ready, so the run is rebuilt here against the
    /// account: frame transactions removed, spent nonces skipped, anchored at the account's next nonce, cut at the
    /// first nonce gap or unpayable base fee.</remarks>
    /// <param name="filtered">Buffer a run that had frame transactions removed is appended to.</param>
    private Run AppendableRun(Transaction[] pending, in UInt256 baseFee, ref ArrayPoolListRef<Transaction> filtered)
    {
        int filteredStart = filtered.Count;
        ReadOnlySpan<Transaction> bySender = WithoutFrameTxs(pending, ref filtered);
        if (bySender.Length == 0) return default;
        // A filtered run is held as indices, not as the span: a later sender's appends may move the buffer.
        Transaction[]? bucket = bySender.Length == pending.Length ? pending : null;

        ulong anchor = headState.GetNonce(bySender[0].SenderAddress!);
        int start = 0;
        // The pool reads an entry under the account nonce as spent rather than blocking, so one can head the
        // bucket while a later entry is what got it admitted.
        while (start < bySender.Length && bySender[start].Nonce < anchor) start++;
        if (start == bySender.Length || bySender[start].Nonce != anchor) return default;

        int length = 0;
        // Buckets are nonce-ordered, so a broken offset can never realign: nothing behind a gap is appendable,
        // and nothing behind an entry the next block would price out is worth the byte cap either.
        while (start + length < bySender.Length
            && bySender[start + length].Nonce == anchor + (ulong)length
            && bySender[start + length].CanPayBaseFee(baseFee))
        {
            length++;
        }

        return new Run(bucket, bucket is null ? filteredStart + start : start, length);
    }

    /// <summary>The sender's pending run with its EIP-8141 frame transactions removed.</summary>
    /// <remarks>
    /// Listing one spends the byte cap without buying censorship resistance, and its EIP-8250 keyed nonce
    /// shares <see cref="Transaction.Nonce"/> while counting per key, breaking the offsets behind it.
    /// A bucket holding none is returned as a span over itself, so only a bucket that holds one is copied.
    /// </remarks>
    private static ReadOnlySpan<Transaction> WithoutFrameTxs(Transaction[] bySender, ref ArrayPoolListRef<Transaction> filtered)
    {
        int kept = 0;
        foreach (Transaction tx in bySender)
            if (!tx.SupportsFrames) kept++;
        if (kept == bySender.Length) return bySender;

        int start = filtered.Count;
        foreach (Transaction tx in bySender)
            if (!tx.SupportsFrames) filtered.Add(tx);
        return filtered.AsSpan().Slice(start, kept);
    }

    /// <summary>The spec of the next block, which fixes its base fee and whether Profile 2 is active.</summary>
    /// <remarks>Approximate at a fork boundary: the next timestamp is not derivable here, so the parent's
    /// stands in and the pre-fork spec is resolved for a post-fork block.</remarks>
    private IReleaseSpec? NextBlockSpec(BlockHeader? parent) =>
        parent is null ? null : specProvider.GetSpec(parent.Number + 1, parent.Timestamp);

    private static InclusionListBytes EncodeTransactionsUpToLimit(in ArrayPoolListRef<Transaction> txs)
    {
        InclusionListBytes result = new(txs.Count);
        try
        {
            int size = 0;
            // EIP-7805 § IL Building: a member should keep its Profile 2 candidates' total VERIFY budget cost
            // within MAX_VERIFY_GAS_PER_IL, which also has every one of them admitted whatever the fill's order.
            ulong verifyBudget = Eip8369Constants.MaxVerifyGasPerIl;
            // The sample orders each sender's run by ascending nonce, so once one entry is skipped the rest
            // of that run is excused whenever the skipped one is absent: budget spent for no extra coverage.
            // A frame candidate is a run of its own, so skipping it drops nothing else.
            HashSet<AddressAsKey>? droppedSenders = null;
            foreach (Transaction tx in txs)
            {
                bool frameCandidate = tx.SupportsFrames;
                if (!frameCandidate && droppedSenders is not null && droppedSenders.Contains(tx.SenderAddress!)) continue;

                ulong verifyCost = frameCandidate ? Eip8369Profile2.VerifyBudgetCost(tx) : 0;
                if (verifyCost > verifyBudget) continue;

                ArrayPoolList<byte> txBytes = InclusionListDecoder.EncodePooled(tx);

                if (size + txBytes.Count > Eip7805Constants.MaxBytesPerInclusionList)
                {
                    txBytes.Dispose();
                    if (!frameCandidate) (droppedSenders ??= []).Add(tx.SenderAddress!);
                    continue;
                }

                size += txBytes.Count;
                verifyBudget -= verifyCost;
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
