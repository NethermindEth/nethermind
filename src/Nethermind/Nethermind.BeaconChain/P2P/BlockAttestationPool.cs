// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.P2P;

/// <summary>The committee aggregates this node validated, kept for the blocks it proposes to include.</summary>
/// <remarks>
/// <para>
/// One aggregate is kept for each data root and committee. An aggregate disjoint from the one held is merged into it; otherwise the one
/// with more attesters is kept, so overlapping aggregates are not unioned. Only aggregates whose signatures verified are added, so a
/// merged signature verifies too.
/// </para>
/// <para>
/// A block includes attestations whose target is its own epoch or the one before (phase0/beacon-chain.md <c>process_attestation</c>),
/// so aggregates of older epochs are dropped, and at most <see cref="MaxAggregates"/> are held; past it an aggregate of a new vote is
/// not added.
/// </para>
/// <para>Thread safety: every member may be called from any thread and concurrently.</para>
/// </remarks>
public sealed class BlockAttestationPool(BeaconChainSpec spec, SlotClock slotClock)
{
    /// <summary>Every committee of two epochs, each for two votes.</summary>
    internal static readonly int MaxAggregates = 2 * 2 * (int)Presets.SlotsPerEpoch * Presets.MaxCommitteesPerSlot;

    private readonly Lock _lock = new();
    private readonly Dictionary<(Hash256 DataRoot, ulong CommitteeIndex), Aggregate> _aggregates = [];
    private ulong _prunedAtEpoch;

    /// <summary>Adds the verified aggregate of one committee, in its <c>beacon_aggregate_and_proof</c> form.</summary>
    internal void Add(Attestation aggregate)
    {
        if (aggregate is not { Data: { } data, AggregationBits: { } bits, CommitteeBits: { } committeeBits } || SingleCommittee(committeeBits) is not { } committeeIndex) return;
        ulong currentEpoch = slotClock.CurrentEpoch;
        if (spec.GetEpoch(data.Slot) + 1 < currentEpoch) return;

        (Hash256, ulong) key = (SszRoots.HashTreeRoot(data), committeeIndex);
        lock (_lock)
        {
            Prune(currentEpoch);
            if (!_aggregates.TryGetValue(key, out Aggregate? held))
            {
                if (_aggregates.Count < MaxAggregates) _aggregates[key] = new Aggregate(data, new BitArray(bits), aggregate.Signature);
                return;
            }

            if (held.Bits.Length != bits.Length) return;
            BitArray overlap = new BitArray(held.Bits).And(bits);
            if (!overlap.HasAnySet())
                _aggregates[key] = new Aggregate(data, new BitArray(held.Bits).Or(bits), AttestationPool.AggregateSignatures([held.Signature, aggregate.Signature]));
            else if (Count(bits) > Count(held.Bits))
                _aggregates[key] = new Aggregate(data, new BitArray(bits), aggregate.Signature);
        }
    }

    /// <summary>
    /// For each vote held of a slot before <paramref name="slot"/>, its committee aggregates combined into one on-chain attestation
    /// (electra/validator.md <c>compute_on_chain_aggregate</c>), with the signatures still to aggregate.
    /// </summary>
    /// <returns>The candidates, whose <see cref="Attestation.Signature"/> is unset: aggregating the returned signatures is left to the few a block includes.</returns>
    internal List<(Attestation Attestation, BlsSignature[] Signatures)> GetOnChainCandidates(ulong slot)
    {
        Dictionary<Hash256, SortedList<ulong, Aggregate>> votes = [];
        lock (_lock)
        {
            Prune(slotClock.CurrentEpoch);
            foreach (((Hash256 dataRoot, ulong committeeIndex), Aggregate aggregate) in _aggregates)
            {
                if (aggregate.Data.Slot >= slot) continue;
                if (!votes.TryGetValue(dataRoot, out SortedList<ulong, Aggregate>? committees)) votes[dataRoot] = committees = [];
                committees.Add(committeeIndex, aggregate);
            }
        }

        List<(Attestation, BlsSignature[])> candidates = [];
        foreach (SortedList<ulong, Aggregate> committees in votes.Values)
        {
            int length = 0;
            foreach (Aggregate aggregate in committees.Values) length += aggregate.Bits.Length;
            BitArray aggregationBits = new(length);
            BitArray committeeBits = new(Presets.MaxCommitteesPerSlot);
            BlsSignature[] signatures = new BlsSignature[committees.Count];
            int offset = 0;
            for (int i = 0; i < committees.Count; i++)
            {
                Aggregate aggregate = committees.Values[i];
                for (int position = 0; position < aggregate.Bits.Length; position++) aggregationBits[offset + position] = aggregate.Bits[position];
                offset += aggregate.Bits.Length;
                committeeBits[(int)committees.Keys[i]] = true;
                signatures[i] = aggregate.Signature;
            }

            candidates.Add((new Attestation { AggregationBits = aggregationBits, Data = committees.Values[0].Data, CommitteeBits = committeeBits }, signatures));
        }

        return candidates;
    }

    internal static int Count(BitArray bits)
    {
        int count = 0;
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i]) count++;
        }

        return count;
    }

    private static ulong? SingleCommittee(BitArray committeeBits)
    {
        ulong? committeeIndex = null;
        for (int i = 0; i < committeeBits.Length; i++)
        {
            if (!committeeBits[i]) continue;
            if (committeeIndex is not null) return null;
            committeeIndex = (ulong)i;
        }

        return committeeIndex;
    }

    // Callers hold _lock.
    private void Prune(ulong currentEpoch)
    {
        if (currentEpoch == _prunedAtEpoch) return;
        _prunedAtEpoch = currentEpoch;
        foreach (((Hash256, ulong) key, Aggregate aggregate) in _aggregates)
        {
            if (spec.GetEpoch(aggregate.Data.Slot) + 1 < currentEpoch) _aggregates.Remove(key);
        }
    }

    private sealed record Aggregate(AttestationData Data, BitArray Bits, BlsSignature Signature);
}
