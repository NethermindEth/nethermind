// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>The latest LMD-GHOST message of a single validator.</summary>
/// <remarks>
/// <see cref="CurrentRoot"/> is the vote currently counted in node weights; <see cref="NextRoot"/> /
/// <see cref="NextEpoch"/> hold the most recent attestation, applied (and copied into
/// <see cref="CurrentRoot"/>) on the next delta computation. <see cref="Hash256.Zero"/> is an alias
/// for "no vote".
/// </remarks>
public struct VoteTracker
{
    public static VoteTracker Unset => new() { CurrentRoot = Hash256.Zero, NextRoot = Hash256.Zero };

    public Hash256 CurrentRoot;
    public Hash256 NextRoot;
    public ulong NextEpoch;

    public readonly bool IsUnset => CurrentRoot == Hash256.Zero && NextRoot == Hash256.Zero && NextEpoch == 0;
}

/// <summary>
/// Per-validator vote storage which grows on demand (Lighthouse's <c>ElasticList&lt;VoteTracker&gt;</c>),
/// plus the <see cref="ComputeDeltas"/> score-change computation.
/// </summary>
public sealed class VoteTrackerList
{
    private VoteTracker[] _votes = [];

    public int Count { get; private set; }

    /// <summary>Returns a mutable reference to the vote of <paramref name="validatorIndex"/>, growing the list if needed.</summary>
    public ref VoteTracker GetMut(ulong validatorIndex)
    {
        int index = checked((int)validatorIndex);
        EnsureSize(index + 1);
        return ref _votes[index];
    }

    /// <summary>Returns the latest (root, target epoch) message of <paramref name="validatorIndex"/>, or <c>null</c> if it never voted.</summary>
    public (Hash256 BlockRoot, ulong TargetEpoch)? LatestMessage(ulong validatorIndex)
    {
        if (validatorIndex >= (ulong)Count) return null;

        VoteTracker vote = _votes[validatorIndex];
        return vote.IsUnset ? null : (vote.NextRoot, vote.NextEpoch);
    }

    /// <summary>
    /// Computes one weight delta per node index in <paramref name="indices"/> from vote changes and
    /// balance changes, committing each pending <see cref="VoteTracker.NextRoot"/> as it goes.
    /// </summary>
    /// <remarks>
    /// Port of Lighthouse's <c>compute_deltas</c>. Votes for roots unknown to
    /// <paramref name="indices"/> are ignored (assumed pre-finalization). A validator in
    /// <paramref name="equivocatingIndices"/> has its current vote deducted once — its
    /// <see cref="VoteTracker.CurrentRoot"/> is then permanently zeroed so the deduction never
    /// repeats and later attestations (which only touch <see cref="VoteTracker.NextRoot"/>) are
    /// never counted.
    /// </remarks>
    public long[] ComputeDeltas(
        IReadOnlyDictionary<Hash256, int> indices,
        IReadOnlyList<ulong> oldBalances,
        IReadOnlyList<ulong> newBalances,
        IReadOnlySet<ulong> equivocatingIndices)
    {
        long[] deltas = new long[indices.Count];
        Span<VoteTracker> votes = _votes.AsSpan(0, Count);

        for (int validatorIndex = 0; validatorIndex < votes.Length; validatorIndex++)
        {
            ref VoteTracker vote = ref votes[validatorIndex];

            // No score change if the validator has never voted or both votes are for the zero hash
            // (an alias for the genesis block).
            if (vote.CurrentRoot == Hash256.Zero && vote.NextRoot == Hash256.Zero) continue;

            if (equivocatingIndices.Contains((ulong)validatorIndex))
            {
                if (vote.CurrentRoot != Hash256.Zero)
                {
                    ulong oldBalance = validatorIndex < oldBalances.Count ? oldBalances[validatorIndex] : 0;
                    if (indices.TryGetValue(vote.CurrentRoot, out int currentIndex))
                    {
                        deltas[currentIndex] = checked(deltas[currentIndex] - (long)oldBalance);
                    }

                    vote.CurrentRoot = Hash256.Zero;
                }

                continue;
            }

            // A validator not in the old balances did not exist yet; one not in the new balances
            // can occur when the justified state moves to a fork that on-boarded fewer validators.
            ulong oldVoteBalance = validatorIndex < oldBalances.Count ? oldBalances[validatorIndex] : 0;
            ulong newVoteBalance = validatorIndex < newBalances.Count ? newBalances[validatorIndex] : 0;

            if (vote.CurrentRoot != vote.NextRoot || oldVoteBalance != newVoteBalance)
            {
                if (indices.TryGetValue(vote.CurrentRoot, out int currentIndex))
                {
                    deltas[currentIndex] = checked(deltas[currentIndex] - (long)oldVoteBalance);
                }

                if (indices.TryGetValue(vote.NextRoot, out int nextIndex))
                {
                    deltas[nextIndex] = checked(deltas[nextIndex] + (long)newVoteBalance);
                }

                vote.CurrentRoot = vote.NextRoot;
            }
        }

        return deltas;
    }

    private void EnsureSize(int size)
    {
        if (size <= Count) return;

        if (size > _votes.Length)
        {
            Array.Resize(ref _votes, Math.Max(Math.Max(4, _votes.Length * 2), size));
        }

        for (int i = Count; i < size; i++)
        {
            _votes[i] = VoteTracker.Unset;
        }

        Count = size;
    }
}
