// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Consensus.Decoders;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;

namespace Nethermind.Merge.Plugin.Handlers;

internal class InclusionListBuilder(ITxPool txPool, IBlockTree blockTree, ISpecProvider specProvider, IReadOnlyStateProvider headState,
    InclusionListProofService? proofs = null)
{
    // Conservative lower bound for an encoded transaction's size.
    private const int MinTransactionSizeBytes = 32;
    // Senders drawn per list. The byte cap, not this, decides how many of them reach the wire.
    private const int SenderSampleCapacity = Eip7805Constants.MaxBytesPerInclusionList / MinTransactionSizeBytes;
    // Senders drawn for dependency-bearing candidates, each contributing at most one.
    internal const int DependencyBearingSenderSample = 64;
    // Dependency-bearing entries of a drawn sender judged before it is passed over.
    internal const int MaxDependencyBearingScannedPerSender = 4;
    // Transaction bytes the dependency-free draw leaves for dependency-bearing entries a held proof covers.
    internal const int ReservedDependencyBearingBytes = Eip7805Constants.MaxBytesPerInclusionList / 4;

    /// <summary>Draws pending transactions for an inclusion list, up to the per-list byte cap.</summary>
    /// <param name="parent">Header the next-block base fee is derived from; the head when null.</param>
    /// <returns>The encoded transactions; the caller owns and disposes them.</returns>
    public InclusionListBytes GetInclusionList(BlockHeader? parent = null)
    {
        using ArrayPoolListRef<Transaction> sample = SampleAppendableTxs(parent);
        return EncodeTransactionsUpToLimit(in sample);
    }

    /// <summary>Draws an EIP-8288 inclusion list: dependency-free transactions as <see cref="GetInclusionList"/> draws them, then
    /// dependency-bearing transactions a held verified proof covers.</summary>
    /// <remarks>The list attaches that proof, so it obligates each dependency-bearing entry it carries (EIPs#12475). EIP-8288 asks
    /// list creators to reserve a share of the transaction bytes for dependency-bearing transactions: when a held proof covers
    /// any, the dependency-free draw leaves them up to <see cref="ReservedDependencyBearingBytes"/>, and they may also take
    /// whatever else it leaves. Without a covering proof the dependency-free draw takes the whole cap and the proof is empty.
    /// Proving is left to the background aggregation cycle: none runs here, as it would not fit
    /// <c>engine_getInclusionListV2</c>'s timeout.</remarks>
    /// <param name="parent">Header the list's target block builds on; the head when null.</param>
    /// <returns>The encoded transactions, which the caller owns and disposes, and the list's proof.</returns>
    public (InclusionListBytes Transactions, RecursiveStarkV1 Proof) GetInclusionListWithProof(BlockHeader? parent = null)
    {
        (HeldInclusionListProof Proof, List<ArrayPoolList<byte>?> Encoded)? covered = CoveredDependencyBearing(parent ?? blockTree.Head?.Header);
        InclusionListBytes? transactions = null;
        try
        {
            int reserved = 0;
            if (covered is { } held)
            {
                foreach (ArrayPoolList<byte>? tx in held.Encoded) reserved += tx!.Count;
                reserved = Math.Min(reserved, ReservedDependencyBearingBytes);
            }
            using (ArrayPoolListRef<Transaction> sample = SampleAppendableTxs(parent))
                transactions = EncodeTransactionsUpToLimit(in sample, Eip7805Constants.MaxBytesPerInclusionList - reserved);
            if (covered is null) return (transactions, RecursiveStarkV1.Empty);
            int size = 0;
            foreach (ArrayPoolList<byte> tx in transactions) size += tx.Count;
            bool appended = false;
            List<ArrayPoolList<byte>?> encoded = covered.Value.Encoded;
            for (int i = 0; i < encoded.Count && size + MinTransactionSizeBytes <= Eip7805Constants.MaxBytesPerInclusionList; i++)
            {
                ArrayPoolList<byte> tx = encoded[i]!;
                if (size + tx.Count > Eip7805Constants.MaxBytesPerInclusionList) continue;
                size += tx.Count;
                transactions.Add(tx);
                // The list owns it now.
                encoded[i] = null;
                appended = true;
            }
            HeldInclusionListProof proof = covered.Value.Proof;
            return (transactions, appended ? new RecursiveStarkV1 { StarkProof = proof.StarkProof, ProofDeps = proof.ProofDeps } : RecursiveStarkV1.Empty);
        }
        catch
        {
            transactions?.Dispose();
            throw;
        }
        finally
        {
            if (covered is { } held)
                foreach (ArrayPoolList<byte>? tx in held.Encoded) tx?.Dispose();
        }
    }

    /// <summary>The held proof a list would attach and the encodings of the dependency-bearing pool transactions it covers, in
    /// candidate order; null when EIP-8288 is inactive at the target fork or no held proof covers a candidate.</summary>
    /// <returns>Encodings the caller owns and disposes.</returns>
    private (HeldInclusionListProof Proof, List<ArrayPoolList<byte>?> Encoded)? CoveredDependencyBearing(BlockHeader? parent)
    {
        if (proofs is null || parent is null) return null;
        // The list constrains the next slot's block: its fork fixes static validity and the AGGREGATED_VK, predicted as
        // for mempool wrappers, so a held wrapper proof verifies under the same verifier.
        IReleaseSpec targetSpec = WrapperTargetFork.Predict(specProvider, parent,
            blockTree.FindParentHeader(parent, BlockTreeLookupOptions.TotalDifficultyNotNeeded));
        if (!targetSpec.IsEip8288Enabled) return null;

        List<(Transaction Tx, List<FrameDependency> Dependencies)> candidates = DependencyBearingCandidates(targetSpec, NextBlockBaseFee(parent));
        if (candidates.Count == 0) return null;
        List<IReadOnlyList<FrameDependency>> dependencies = new(candidates.Count);
        foreach ((_, List<FrameDependency> deps) in candidates) dependencies.Add(deps);
        if (proofs.ChooseHeldProof(dependencies, targetSpec) is not { } proof) return null;

        List<ArrayPoolList<byte>?> encoded = [];
        int size = 0;
        foreach ((Transaction tx, List<FrameDependency> deps) in candidates)
        {
            if (!proof.Covers(deps)) continue;
            ArrayPoolList<byte> txBytes = InclusionListDecoder.EncodePooled(tx);
            if (txBytes.Count > Eip7805Constants.MaxBytesPerInclusionList - size)
            {
                txBytes.Dispose();
                continue;
            }
            size += txBytes.Count;
            encoded.Add(txBytes);
        }
        return encoded.Count == 0 ? null : (proof, encoded);
    }

    /// <summary>Up to <see cref="DependencyBearingSenderSample"/> pending dependency-bearing transactions the next block could
    /// append, at most one per sender, in random order, with their dependencies.</summary>
    /// <remarks>Senders holding a dependency-bearing transaction are reservoir-sampled from the pool snapshot before any state
    /// read, validation or dependency extraction, so the timed call's work is bounded by the sample, not the pool, and no sender
    /// takes more than one place. A drawn sender contributes its first dependency-bearing entry, among the first
    /// <see cref="MaxDependencyBearingScannedPerSender"/>, that passes the target fork's static validity, so a covering proof
    /// obligates it, pays the next base fee and carries its sender's next nonce: the account nonce, or for an EIP-8250 keyed
    /// transaction the current sequence of every key it selects. Blob-carrying transactions stay out, as from the
    /// dependency-free draw.</remarks>
    private List<(Transaction Tx, List<FrameDependency> Dependencies)> DependencyBearingCandidates(IReleaseSpec targetSpec, in UInt256 baseFee)
    {
        const int capacity = DependencyBearingSenderSample;
        Random rnd = Random.Shared;
        Transaction[][] drawn = new Transaction[capacity][];
        int count = 0, seen = 0;
        foreach (Transaction[] bucket in txPool.GetPendingTransactionsBySender().Values)
        {
            if (!HoldsDependencyBearing(bucket)) continue;
            if (count < capacity) drawn[count++] = bucket;
            else
            {
                int j = rnd.Next(seen + 1);
                if (j < capacity) drawn[j] = bucket;
            }
            seen++;
        }
        rnd.Shuffle(drawn.AsSpan(0, count));

        List<(Transaction, List<FrameDependency>)> candidates = new(count);
        for (int i = 0; i < count; i++)
        {
            ulong? accountNonce = null;
            int scanned = 0;
            foreach (Transaction tx in drawn[i])
            {
                if (!IsDependencyBearingCandidate(tx)) continue;
                if (++scanned > MaxDependencyBearingScannedPerSender) break;
                if (!tx.CanPayBaseFee(baseFee) || !InclusionListProofValidator.IsStaticallyValid(tx, targetSpec)) continue;
                bool ready = KeyedNonceManager.UsesKeyedNonce(tx)
                    ? KeyedNonceManager.IsNonceSetValid(headState, tx.SenderAddress!, tx.NonceKeys!, tx.Nonce)
                    : tx.Nonce == (accountNonce ??= headState.GetNonce(tx.SenderAddress!));
                if (!ready) continue;
                List<FrameDependency> dependencies = Eip8288Dependencies.ForTransaction(tx, FrameTxSigHash.ComputeValue);
                if (dependencies.Count == 0) continue;
                candidates.Add((tx, dependencies));
                break;
            }
        }
        return candidates;

        static bool HoldsDependencyBearing(Transaction[] bucket)
        {
            foreach (Transaction tx in bucket)
                if (IsDependencyBearingCandidate(tx)) return true;
            return false;
        }
    }

    private static bool IsDependencyBearingCandidate(Transaction tx) =>
        tx.SupportsFrames && !tx.CarriesBlobs && tx.SenderAddress is not null && Eip8288Dependencies.IsDependencyBearing(tx);

    /// <summary>Draws candidate transactions for the list, round-robin across the drawn senders.</summary>
    /// <remarks>Restricted to each sender's appendable run, since nothing else could be appended. Drawn
    /// uniformly, not by fee: a fee-ordered draw drops what a builder passes over.
    /// Not gated on pool revalidation: the ready-tx snapshot applies no per-transaction spec check, so around a
    /// fork the draw can include entries the new spec rejects — either because the pool's background revalidation
    /// is still catching up, or because the target block is the activation slot itself, whose timestamp is
    /// not known from the parent header. The only cost is wasted list bytes.</remarks>
    private ArrayPoolListRef<Transaction> SampleAppendableTxs(BlockHeader? parent)
    {
        const int capacity = SenderSampleCapacity;
        Random rnd = Random.Shared;
        UInt256 baseFee = NextBlockBaseFee(parent);

        // Reservoir over senders rather than over their runs, so the pool's size costs no allocation and no
        // state read: only the drawn senders below pay for one.
        using ArrayPoolListRef<Transaction[]> drawn = new(capacity);
        int seen = 0;
        // The pool routes blob txs to separate storage this snapshot never reads. Frame txs can remain in
        // mixed sender buckets and are removed below.
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

        // Runs are held as ranges: one over the sender's own bucket where nothing had to be filtered out,
        // and otherwise over a single shared buffer the filtered entries are appended to.
        ArrayPoolListRef<Transaction> filtered = new(capacity);
        try
        {
            using ArrayPoolListRef<Run> runs = new(capacity);
            foreach (Transaction[] pending in drawn)
            {
                Run run = AppendableRun(pending, in baseFee, ref filtered);
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
    /// Its EIP-8250 keyed nonce shares <see cref="Transaction.Nonce"/> while counting per key, breaking the offsets
    /// behind it. A dependency-bearing one binds only under a covering proof, so <see cref="GetInclusionListWithProof"/>
    /// draws those separately.
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

    /// <summary>The base fee the next block will charge.</summary>
    /// <remarks>Approximate at a fork boundary: the next timestamp is not derivable here, so the parent's
    /// stands in and pre-fork EIP-1559 parameters are resolved for a post-fork block.</remarks>
    private UInt256 NextBlockBaseFee(BlockHeader? parent)
    {
        parent ??= blockTree.Head?.Header;
        return parent is null ? UInt256.Zero : BaseFeeCalculator.Calculate(parent, specProvider.GetSpec(parent.Number + 1, parent.Timestamp));
    }

    private static InclusionListBytes EncodeTransactionsUpToLimit(in ArrayPoolListRef<Transaction> txs,
        int limit = Eip7805Constants.MaxBytesPerInclusionList)
    {
        InclusionListBytes result = new(txs.Count);
        try
        {
            int size = 0;
            // The sample orders each sender's run by ascending nonce, so once one entry is skipped the rest
            // of that run is excused whenever the skipped one is absent: budget spent for no extra coverage.
            HashSet<AddressAsKey>? droppedSenders = null;
            foreach (Transaction tx in txs)
            {
                if (droppedSenders is not null && droppedSenders.Contains(tx.SenderAddress!)) continue;

                ArrayPoolList<byte> txBytes = InclusionListDecoder.EncodePooled(tx);

                if (size + txBytes.Count > limit)
                {
                    txBytes.Dispose();
                    (droppedSenders ??= []).Add(tx.SenderAddress!);
                    continue;
                }

                size += txBytes.Count;
                result.Add(txBytes);

                // No possible tx can fit in the remaining space.
                if (size + MinTransactionSizeBytes > limit)
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
