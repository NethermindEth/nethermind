// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.BeaconChain.P2P;

/// <summary>The unaggregated attestations this node validated, one aggregate for each vote, for local aggregators to publish.</summary>
/// <remarks>
/// <para>
/// Attestations with the same data root and committee form one aggregate: each committee position is counted once, so the
/// aggregate covers every distinct attester held, which is the best aggregate (phase0/validator.md "Construct aggregate").
/// Only attestations whose signature verified are added, so aggregating never fails.
/// </para>
/// <para>
/// Aggregates are due two thirds into their slot, so only the current and previous slot are held, and at most
/// <see cref="MaxAttestations"/> attestations in all; past it an attestation is not added.
/// </para>
/// <para>Thread safety: every member may be called from any thread and concurrently.</para>
/// </remarks>
public sealed class AttestationPool(SlotClock slotClock)
{
    /// <summary>The largest committees of every committee of one slot; the two slots held of a registry of two million validators fit.</summary>
    internal const int MaxAttestations = Presets.MaxCommitteesPerSlot * Presets.MaxValidatorsPerCommittee;

    private readonly Lock _lock = new();
    private readonly Dictionary<(ulong Slot, Hash256 DataRoot, ulong CommitteeIndex), Aggregate> _aggregates = [];
    private int _attestationCount;
    private ulong _prunedAtSlot;

    /// <summary>Adds the verified attestation of the member at <paramref name="position"/> of a committee of <paramref name="committeeLength"/>.</summary>
    /// <param name="dataRoot">The <c>hash_tree_root</c> of <paramref name="data"/>.</param>
    internal void Add(AttestationData data, Hash256 dataRoot, ulong committeeIndex, int committeeLength, int position, BlsSignature signature)
    {
        ulong currentSlot = slotClock.CurrentSlot;
        if (data.Slot + 1 < currentSlot) return;

        lock (_lock)
        {
            Prune(currentSlot);
            if (_attestationCount >= MaxAttestations) return;

            (ulong, Hash256, ulong) key = (data.Slot, dataRoot, committeeIndex);
            if (!_aggregates.TryGetValue(key, out Aggregate? aggregate))
                _aggregates[key] = aggregate = new Aggregate(data, committeeLength);
            if (aggregate.Bits.Length != committeeLength || aggregate.Bits[position]) return;

            aggregate.Bits[position] = true;
            aggregate.Signatures.Add(signature);
            _attestationCount++;
        }
    }

    /// <summary>The aggregate of every attestation held for the vote, or <c>null</c> when none is held.</summary>
    internal Attestation? GetAggregate(ulong slot, Hash256 dataRoot, ulong committeeIndex)
    {
        AttestationData data;
        BitArray bits;
        BlsSignature[] signatures;
        lock (_lock)
        {
            Prune(slotClock.CurrentSlot);
            if (!_aggregates.TryGetValue((slot, dataRoot, committeeIndex), out Aggregate? aggregate)) return null;
            (data, bits, signatures) = (aggregate.Data, new BitArray(aggregate.Bits), [.. aggregate.Signatures]);
        }

        BlsSigner.Signature aggregated = new();
        aggregated.Decode(signatures[0].Bytes);
        for (int i = 1; i < signatures.Length; i++)
        {
            BlsSigner.Signature signature = new();
            signature.Decode(signatures[i].Bytes);
            aggregated.Aggregate(signature);
        }

        return new Attestation
        {
            AggregationBits = bits,
            Data = data,
            Signature = new BlsSignature(aggregated.Bytes),
            CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [(int)committeeIndex] = true },
        };
    }

    // Callers hold _lock.
    private void Prune(ulong currentSlot)
    {
        if (currentSlot == _prunedAtSlot) return;
        _prunedAtSlot = currentSlot;
        foreach (((ulong Slot, Hash256, ulong) key, Aggregate aggregate) in _aggregates)
        {
            if (key.Slot + 1 < currentSlot && _aggregates.Remove(key))
                _attestationCount -= aggregate.Signatures.Count;
        }
    }

    private sealed class Aggregate(AttestationData data, int committeeLength)
    {
        public AttestationData Data => data;
        public BitArray Bits { get; } = new(committeeLength);
        public List<BlsSignature> Signatures { get; } = [];
    }
}
