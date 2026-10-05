// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public static VoteTracker Unset => new()
    {
        CurrentRoot = Hash256.Zero,
        NextRoot = Hash256.Zero,
        CurrentStatus = ForkChoicePayloadStatus.Pending,
        NextStatus = ForkChoicePayloadStatus.Pending,
    };

    public Hash256 CurrentRoot;
    public Hash256 NextRoot;
    public ulong NextEpoch;

    /// <summary>The attestation slot of the most recent message; the Gloas <c>LatestMessage.slot</c>.</summary>
    public ulong NextSlot;

    /// <summary>The node of <see cref="NextRoot"/> the most recent message supports; the spec's <c>get_supported_node</c>.</summary>
    public ForkChoicePayloadStatus NextStatus;

    /// <summary>The node of <see cref="CurrentRoot"/> currently counted in the payload-status weights.</summary>
    public ForkChoicePayloadStatus CurrentStatus;

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
    /// Every vote counts in its root's <see cref="ScoreDeltas.Weight"/>; a vote supporting the EMPTY or
    /// FULL node also counts in that bucket (specs/gloas/fork-choice.md <c>get_supported_node</c>), so a
    /// status change on the same root moves bucket weight while netting zero on the root.
    /// </remarks>
    public ScoreDeltas ComputeDeltas(
        IReadOnlyDictionary<Hash256, int> indices,
        IReadOnlyList<ulong> oldBalances,
        IReadOnlyList<ulong> newBalances,
        IReadOnlySet<ulong> equivocatingIndices)
    {
        long[] deltas = new long[indices.Count];
        long[] emptyDeltas = new long[indices.Count];
        long[] fullDeltas = new long[indices.Count];
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
                        AddToBucket(emptyDeltas, fullDeltas, currentIndex, vote.CurrentStatus, checked(-(long)oldBalance));
                    }

                    vote.CurrentRoot = Hash256.Zero;
                    vote.CurrentStatus = ForkChoicePayloadStatus.Pending;
                }

                continue;
            }

            // A validator not in the old balances did not exist yet; one not in the new balances
            // can occur when the justified state moves to a fork that on-boarded fewer validators.
            ulong oldVoteBalance = validatorIndex < oldBalances.Count ? oldBalances[validatorIndex] : 0;
            ulong newVoteBalance = validatorIndex < newBalances.Count ? newBalances[validatorIndex] : 0;

            if (vote.CurrentRoot != vote.NextRoot || vote.CurrentStatus != vote.NextStatus || oldVoteBalance != newVoteBalance)
            {
                if (indices.TryGetValue(vote.CurrentRoot, out int currentIndex))
                {
                    deltas[currentIndex] = checked(deltas[currentIndex] - (long)oldVoteBalance);
                    AddToBucket(emptyDeltas, fullDeltas, currentIndex, vote.CurrentStatus, checked(-(long)oldVoteBalance));
                }

                if (indices.TryGetValue(vote.NextRoot, out int nextIndex))
                {
                    deltas[nextIndex] = checked(deltas[nextIndex] + (long)newVoteBalance);
                    AddToBucket(emptyDeltas, fullDeltas, nextIndex, vote.NextStatus, (long)newVoteBalance);
                }

                vote.CurrentRoot = vote.NextRoot;
                vote.CurrentStatus = vote.NextStatus;
            }
        }

        return new ScoreDeltas(deltas, emptyDeltas, fullDeltas);
    }

    /// <summary>Adds <paramref name="delta"/> to the EMPTY or FULL bucket of a node; a PENDING vote has no bucket.</summary>
    internal static void AddToBucket(long[] emptyDeltas, long[] fullDeltas, int index, ForkChoicePayloadStatus status, long delta)
    {
        switch (status)
        {
            case ForkChoicePayloadStatus.Empty:
                emptyDeltas[index] = checked(emptyDeltas[index] + delta);
                break;
            case ForkChoicePayloadStatus.Full:
                fullDeltas[index] = checked(fullDeltas[index] + delta);
                break;
        }
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

/// <summary>Per-node weight deltas indexed like <see cref="ProtoArray.Nodes"/>.</summary>
/// <param name="Weight">Deltas of each block's PENDING weight, which every vote for the block or a descendant counts in.</param>
/// <param name="Empty">Deltas from votes supporting each block's EMPTY node directly.</param>
/// <param name="Full">Deltas from votes supporting each block's FULL node directly.</param>
public readonly record struct ScoreDeltas(long[] Weight, long[] Empty, long[] Full);
