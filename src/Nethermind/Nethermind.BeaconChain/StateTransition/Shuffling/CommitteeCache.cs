// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.StateTransition.Shuffling;

/// <summary>
/// The shuffled committee assignment for one epoch: every beacon committee of the epoch is a
/// contiguous slice of one shuffled active-validator list.
/// </summary>
/// <remarks>
/// Mirrors Lighthouse's <c>CommitteeCache</c>: instead of computing committees per slot via
/// <c>compute_committee</c>, the whole active set is shuffled once with the bulk
/// <see cref="SwapOrNotShuffle.ShuffleList"/> and committees are returned as spans into it.
/// Instances are immutable after <see cref="Build"/>.
/// </remarks>
public sealed class CommitteeCache
{
    private readonly int[] _shuffling;

    private CommitteeCache(ulong epoch, int[] shuffling, int committeesPerSlot)
    {
        Epoch = epoch;
        _shuffling = shuffling;
        CommitteesPerSlot = committeesPerSlot;
    }

    /// <summary>The epoch this cache was built for.</summary>
    public ulong Epoch { get; }

    public int CommitteesPerSlot { get; }

    public int ActiveValidatorCount => _shuffling.Length;

    public int EpochCommitteeCount => CommitteesPerSlot * (int)Presets.SlotsPerEpoch;

    /// <summary>The shuffled active validator indices for the epoch (not in ascending order).</summary>
    public ReadOnlySpan<int> ShuffledIndices => _shuffling;

    /// <summary>Builds the committee shuffling for <paramref name="epoch"/> from the state's active set and attester seed.</summary>
    /// <exception cref="BeaconStateException">No validator is active at <paramref name="epoch"/>.</exception>
    public static CommitteeCache Build(BeaconStateFulu state, ulong epoch)
    {
        int[] activeIndices = state.GetActiveValidatorIndices(epoch);
        if (activeIndices.Length == 0)
            throw new BeaconStateException($"No active validators at epoch {epoch}");

        Hash256 seed = state.GetSeed(epoch, DomainType.BeaconAttester);
        SwapOrNotShuffle.ShuffleList(activeIndices, seed.Bytes);

        return new CommitteeCache(epoch, activeIndices, GetCommitteeCountPerSlot(activeIndices.Length));
    }

    /// <summary>Returns the spec <c>get_committee_count_per_slot</c> for the given active validator count.</summary>
    public static int GetCommitteeCountPerSlot(int activeValidatorCount) =>
        Math.Clamp(activeValidatorCount / (int)Presets.SlotsPerEpoch / Presets.TargetCommitteeSize, 1, Presets.MaxCommitteesPerSlot);

    /// <summary>Returns the members of committee <paramref name="index"/> at <paramref name="slot"/>.</summary>
    /// <exception cref="BeaconStateException">The slot is outside the cached epoch or the index is out of range.</exception>
    public ReadOnlySpan<int> GetBeaconCommittee(ulong slot, int index)
    {
        if (BeaconStateAccessors.ComputeEpochAtSlot(slot) != Epoch)
            throw new BeaconStateException($"Slot {slot} is not in cached epoch {Epoch}");
        if ((uint)index >= (uint)CommitteesPerSlot)
            throw new BeaconStateException($"Committee index {index} out of range ({CommitteesPerSlot} committees per slot)");

        int indexInEpoch = (int)(slot % Presets.SlotsPerEpoch) * CommitteesPerSlot + index;
        // 64-bit math: shuffling length (≤2^24) times committee count (≤2^11) overflows int.
        int start = (int)((long)_shuffling.Length * indexInEpoch / EpochCommitteeCount);
        int end = (int)((long)_shuffling.Length * (indexInEpoch + 1) / EpochCommitteeCount);
        return _shuffling.AsSpan(start..end);
    }
}

/// <summary>
/// A small least-recently-used cache of <see cref="CommitteeCache"/> entries keyed by
/// <c>(epoch, shuffling decision root)</c>, so equal shufflings are shared across forks and slots.
/// </summary>
/// <remarks>
/// Not thread-safe: each owner (e.g. a state-transition context) should hold its own instance.
/// Only epochs whose decision root is reachable from the state (previous/current/next epoch)
/// can be cached; see <see cref="BeaconStateAccessors.GetShufflingDecisionRoot"/>.
/// </remarks>
public sealed class CommitteeCacheLru(int capacity = CommitteeCacheLru.DefaultCapacity)
{
    public const int DefaultCapacity = 4;

    private readonly List<(ulong Epoch, Hash256 DecisionRoot, CommitteeCache Cache)> _entries = new(capacity);

    /// <summary>Returns the cached committees for <paramref name="epoch"/>, building them if absent.</summary>
    public CommitteeCache GetOrBuild(BeaconStateFulu state, ulong epoch)
    {
        Hash256 decisionRoot = state.GetShufflingDecisionRoot(epoch);
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Epoch == epoch && _entries[i].DecisionRoot == decisionRoot)
            {
                (ulong, Hash256, CommitteeCache Cache) entry = _entries[i];
                _entries.RemoveAt(i);
                _entries.Add(entry);
                return entry.Cache;
            }
        }

        CommitteeCache built = CommitteeCache.Build(state, epoch);
        if (_entries.Count == capacity)
            _entries.RemoveAt(0);
        _entries.Add((epoch, decisionRoot, built));
        return built;
    }
}
