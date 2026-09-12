// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Consensus.Decoders;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

public class InclusionListBuilder(ITxPool txPool, IBlockTree blockTree, ISpecProvider specProvider, IMergeConfig mergeConfig)
{
    // Conservative lower bound for an encoded transaction's size.
    private const int MinTransactionSizeBytes = 32;
    // Senders drawn per list. The byte cap, not this, decides how many of them reach the wire.
    private const int SenderSampleCapacity = Eip7805Constants.MaxBytesPerInclusionList / MinTransactionSizeBytes;

    // Orders the age cohort newest first, so its head is the member an older sender displaces.
    private static readonly IComparer<ulong> NewestFirst = Comparer<ulong>.Create(static (a, b) => b.CompareTo(a));

    private readonly int _oldestSenderDraw = OldestSenderDraw(mergeConfig.InclusionListOldestSenderShare);
    private readonly int _oldestSenderCount = int.Max(0, mergeConfig.InclusionListOldestSenderCount);

    /// <summary>Slots of the draw <see cref="IMergeConfig.InclusionListOldestSenderShare"/> reserves.</summary>
    /// <remarks>Rounded up, so a share worth less than a whole slot still turns the tier on: truncating it to
    /// zero would silently ship the default on a knob an operator is calibrating.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The share is not a number between 0 and 1.</exception>
    private static int OldestSenderDraw(double share)
    {
        if (!double.IsFinite(share) || share is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(IMergeConfig.InclusionListOldestSenderShare), share,
                $"{nameof(IMergeConfig.InclusionListOldestSenderShare)} must be between 0 and 1.");
        }

        return (int)double.Ceiling(share * SenderSampleCapacity);
    }

    /// <summary>Draws pending transactions for an inclusion list, up to the per-list byte cap.</summary>
    /// <param name="parent">Header the next-block base fee is derived from; the head when null.</param>
    /// <returns>The encoded transactions; the caller owns and disposes them.</returns>
    public InclusionListBytes GetInclusionList(BlockHeader? parent = null)
    {
        using ArrayPoolListRef<Transaction> sample = SampleAppendableTxs(parent);
        return EncodeTransactionsUpToLimit(in sample);
    }

    /// <summary>Draws candidate transactions for the list, round-robin across the drawn senders.</summary>
    /// <remarks>Restricted to each sender's appendable run from its next nonce, since nothing else could be
    /// appended. Never drawn by fee: a fee-ordered draw drops what a builder passes over. See
    /// <see cref="DrawSenders"/> for how the senders themselves are picked.</remarks>
    private ArrayPoolListRef<Transaction> SampleAppendableTxs(BlockHeader? parent)
    {
        const int capacity = SenderSampleCapacity;
        UInt256 baseFee = NextBlockBaseFee(parent);

        // Blob txs cannot appear here: TxPool routes them to a separate pool this snapshot does not read.
        using ArrayPoolListRef<Transaction[]> senders = DrawSenders(txPool.GetPendingTransactionsBySender(filterToReadyTx: true, baseFee));

        using ArrayPoolListRef<int> runLengths = new(capacity);
        foreach (Transaction[] bySender in senders) runLengths.Add(AppendableRunLength(bySender, in baseFee));

        // Take one nonce per sender per round rather than draining each run in turn, so a single account
        // with a long ready run cannot spend the byte cap before the other drawn senders are represented.
        ArrayPoolListRef<Transaction> sample = new(capacity);
        for (int round = 0; ; round++)
        {
            bool advanced = false;
            for (int i = 0; i < senders.Count; i++)
            {
                if (round >= runLengths[i]) continue;

                sample.Add(senders[i][round]);
                advanced = true;
                if (sample.Count == capacity) return sample;
            }
            if (!advanced) return sample;
        }
    }

    /// <summary>Draws up to <see cref="SenderSampleCapacity"/> of the pending senders, in random order.</summary>
    /// <remarks>
    /// Uniform over the pool unless <see cref="IMergeConfig.InclusionListOldestSenderShare"/> reserves part of
    /// the draw for a uniform sample of the longest-pending senders. The reservation is a floor, not a quota:
    /// the cohort takes the larger of that share and the share a uniform draw would have given it, so raising
    /// the share can never leave it worse represented than leaving the tier off. Since the byte cap keeps only
    /// a random prefix of the draw, the cohort's share of the draw is its share of the list, which is what
    /// lifts the odds of a transaction a builder keeps passing over. EIP-7805 leaves the strategy to the
    /// implementer and names pending time as one; it is off by default because age is cheap to manufacture, so
    /// the tier costs the honest sender its share of the draw exactly when the pool is being flooded.
    /// </remarks>
    private ArrayPoolListRef<Transaction[]> DrawSenders(IDictionary<AddressAsKey, Transaction[]> pending)
    {
        // Nothing to reserve when the cohort is the whole pool, which also leaves the bound below defined.
        if (_oldestSenderDraw == 0 || _oldestSenderCount == 0 || pending.Count <= _oldestSenderCount)
            return SampleUniformly(pending);

        ulong bound = OldestSenderBound(pending, _oldestSenderCount);
        // Not `using`: a reservoir is offered to by reference, which a using variable cannot be.
        ArrayPoolListRef<Transaction[]> oldest = new(SenderSampleCapacity);
        ArrayPoolListRef<Transaction[]> rest = new(SenderSampleCapacity);
        try
        {
            int oldestSeen = 0;
            int restSeen = 0;
            foreach (Transaction[] bySender in pending.Values)
            {
                if (bySender[0].PoolIndex <= bound) Offer(ref oldest, ref oldestSeen, bySender);
                else Offer(ref rest, ref restSeen, bySender);
            }

            // Both tiers are read as a prefix below, so each has to be shuffled to sample it.
            Random.Shared.Shuffle(oldest.AsSpan());
            Random.Shared.Shuffle(rest.AsSpan());

            ArrayPoolListRef<Transaction[]> senders = new(SenderSampleCapacity);
            // Floored at the slots a uniform draw would have given the cohort, so reserving cannot demote what it
            // is meant to promote. Both operands are pre-reservoir: capped counts would leak `rest`'s compression in.
            int uniformDraw = (int)((long)SenderSampleCapacity * oldestSeen / pending.Count);
            int reserved = int.Min(int.Max(_oldestSenderDraw, uniformDraw), oldest.Count);
            Take(ref senders, oldest.AsSpan()[..reserved]);
            Take(ref senders, rest.AsSpan());
            // Either tier spends what the other leaves rather than shrinking the draw.
            Take(ref senders, oldest.AsSpan()[reserved..]);

            // The tier is a share of the draw, not of its order: the byte cap keeps only a prefix of it.
            Random.Shared.Shuffle(senders.AsSpan());
            return senders;
        }
        finally
        {
            oldest.Dispose();
            rest.Dispose();
        }

        static void Take(ref ArrayPoolListRef<Transaction[]> draw, ReadOnlySpan<Transaction[]> tier) =>
            draw.AddRange(tier[..int.Min(tier.Length, SenderSampleCapacity - draw.Count)]);
    }

    /// <summary>Reservoir-samples the pending senders, uniformly and in uniformly random order.</summary>
    /// <remarks>Over senders rather than over their runs, so the pool's size costs no allocation: only the
    /// drawn senders pay for a slot. Shuffled because the byte-cap loop treats position as priority, so
    /// membership alone would not spread the draw.</remarks>
    private static ArrayPoolListRef<Transaction[]> SampleUniformly(IDictionary<AddressAsKey, Transaction[]> pending)
    {
        ArrayPoolListRef<Transaction[]> senders = new(SenderSampleCapacity);
        int seen = 0;
        foreach (Transaction[] bySender in pending.Values) Offer(ref senders, ref seen, bySender);

        Random.Shared.Shuffle(senders.AsSpan());
        return senders;
    }

    /// <summary>Offers one sender to a reservoir holding a uniform sample of what it has been offered.</summary>
    /// <param name="seen">Senders offered to this reservoir so far; advanced by the call.</param>
    private static void Offer(ref ArrayPoolListRef<Transaction[]> reservoir, ref int seen, Transaction[] bySender)
    {
        if (reservoir.Count < SenderSampleCapacity)
        {
            reservoir.Add(bySender);
        }
        else
        {
            int j = Random.Shared.Next(seen + 1);
            if (j < SenderSampleCapacity) reservoir[j] = bySender;
        }

        seen++;
    }

    /// <summary>The highest <see cref="Transaction.PoolIndex"/> among the <paramref name="cohortSize"/> senders
    /// whose next pending nonce reached this pool first.</summary>
    /// <remarks>
    /// A queue bounded to the cohort, so selecting it costs one pass and no sort of the pool. Selecting by rank
    /// rather than against an absolute index keeps the cohort well defined across the two ways the sequence lies:
    /// it restarts from zero with the process, and a reorg re-admits its transactions, re-stamping them as the
    /// newest in the pool. Neither can pass a new arrival off as old: a reorg costs its transactions their place
    /// in the cohort, and a restart, since nothing persists the non-blob pool, ranks every sender by arrival
    /// since startup until the pool turns over.
    /// </remarks>
    private static ulong OldestSenderBound(IDictionary<AddressAsKey, Transaction[]> pending, int cohortSize)
    {
        PriorityQueue<ulong, ulong> cohort = new(cohortSize, NewestFirst);
        foreach (Transaction[] bySender in pending.Values)
        {
            ulong poolIndex = bySender[0].PoolIndex;
            if (cohort.Count < cohortSize) cohort.Enqueue(poolIndex, poolIndex);
            else if (poolIndex < cohort.Peek()) cohort.EnqueueDequeue(poolIndex, poolIndex);
        }

        // The caller only asks for a cohort smaller than the pool, so the queue is never empty here.
        return cohort.Peek();
    }

    /// <summary>How many leading transactions of <paramref name="bySender"/> the next block could append.</summary>
    /// <remarks>Buckets are nonce-ordered, so a broken offset can never realign: nothing behind a nonce gap is
    /// appendable, and nothing behind an entry the next block would price out is worth the byte cap either.
    /// The pool vouches for the first entry alone, so both are re-checked from there.</remarks>
    private static int AppendableRunLength(Transaction[] bySender, in UInt256 baseFee)
    {
        ulong anchor = bySender[0].Nonce;
        int length = 0;
        while (length < bySender.Length
            && bySender[length].Nonce == anchor + (ulong)length
            && bySender[length].CanPayBaseFee(baseFee))
        {
            length++;
        }

        return length;
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
