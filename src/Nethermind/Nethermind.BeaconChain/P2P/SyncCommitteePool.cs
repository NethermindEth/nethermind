// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.P2P;

/// <summary>
/// The sync committee messages and contributions this node validated: one contribution aggregated from the messages of each subcommittee
/// vote, for local aggregators to publish, and the best contribution received for each, for a block to include.
/// </summary>
/// <remarks>
/// <para>
/// A vote is a slot, a block root and a subcommittee. A member's message sets the bit of each of its positions in the subcommittee and counts
/// its signature once for each (altair/validator.md "Aggregation bits", "Signature"), so the aggregate covers every distinct position held.
/// Only messages and contributions whose signatures verified are added, so aggregating never fails.
/// </para>
/// <para>
/// Contributions are due two thirds into their slot and included in the block of the next one, so only the current and previous slot are
/// held: past <see cref="MaxSignatures"/> message signatures no further message is added, and past <see cref="MaxContributions"/> votes
/// no contribution for another vote.
/// </para>
/// <para>Thread safety: every member may be called from any thread and concurrently.</para>
/// </remarks>
public sealed class SyncCommitteePool(SlotClock slotClock)
{
    /// <summary>Every committee position of two slots, each for two block roots.</summary>
    internal const int MaxSignatures = 2 * 2 * Presets.SyncCommitteeSize;

    /// <summary>Every subcommittee of two slots, each for four block roots.</summary>
    internal const int MaxContributions = 2 * 4 * SyncCommitteeContribution.SubnetCount;

    private readonly Lock _lock = new();
    private readonly Dictionary<(ulong Slot, Hash256 BlockRoot, ulong SubcommitteeIndex), Aggregate> _messages = [];
    private readonly Dictionary<(ulong Slot, Hash256 BlockRoot, ulong SubcommitteeIndex), SyncCommitteeContribution> _bestContributions = [];
    private int _signatureCount;
    private ulong _prunedAtSlot;

    /// <summary>Adds the verified message of a member holding <paramref name="positions"/> in subcommittee <paramref name="subcommitteeIndex"/>.</summary>
    /// <param name="positions">The member's positions within the subcommittee, each below <see cref="SyncCommitteeContribution.SubcommitteeSize"/>.</param>
    internal void AddMessage(SyncCommitteeMessage message, ulong subcommitteeIndex, IReadOnlyList<int> positions)
    {
        ulong currentSlot = slotClock.CurrentSlot;
        if (message.Slot + 1 < currentSlot) return;

        lock (_lock)
        {
            Prune(currentSlot);
            if (_signatureCount >= MaxSignatures) return;

            (ulong, Hash256, ulong) key = (message.Slot, message.BeaconBlockRoot!, subcommitteeIndex);
            if (!_messages.TryGetValue(key, out Aggregate? aggregate))
                _messages[key] = aggregate = new Aggregate();
            foreach (int position in positions)
            {
                if (aggregate.Bits[position]) continue;
                aggregate.Bits[position] = true;
                aggregate.Signatures.Add(message.Signature);
                _signatureCount++;
            }
        }
    }

    /// <summary>The contribution aggregated from every message held for the vote, or <c>null</c> when none is held.</summary>
    internal SyncCommitteeContribution? GetContribution(ulong slot, Hash256 blockRoot, ulong subcommitteeIndex)
    {
        BitArray bits;
        BlsSignature[] signatures;
        lock (_lock)
        {
            Prune(slotClock.CurrentSlot);
            if (!_messages.TryGetValue((slot, blockRoot, subcommitteeIndex), out Aggregate? aggregate) || aggregate.Signatures.Count == 0) return null;
            (bits, signatures) = (new BitArray(aggregate.Bits), [.. aggregate.Signatures]);
        }

        return new SyncCommitteeContribution
        {
            Slot = slot,
            BeaconBlockRoot = blockRoot,
            SubcommitteeIndex = subcommitteeIndex,
            AggregationBits = bits,
            Signature = AttestationPool.AggregateSignatures(signatures),
        };
    }

    /// <summary>Keeps the verified <paramref name="contribution"/> when it has more participants than the best held for its vote.</summary>
    internal void AddContribution(SyncCommitteeContribution contribution)
    {
        ulong currentSlot = slotClock.CurrentSlot;
        if (contribution.Slot + 1 < currentSlot) return;

        lock (_lock)
        {
            Prune(currentSlot);
            (ulong, Hash256, ulong) key = (contribution.Slot, contribution.BeaconBlockRoot!, contribution.SubcommitteeIndex);
            if (_bestContributions.TryGetValue(key, out SyncCommitteeContribution? best)
                ? CountParticipants(best) >= CountParticipants(contribution)
                : _bestContributions.Count >= MaxContributions)
                return;
            _bestContributions[key] = contribution;
        }
    }

    /// <summary>The contribution with the most participants received for the vote, or <c>null</c> when none is held.</summary>
    /// <remarks>
    /// What a proposer combines into its block's <c>SyncAggregate</c>: one for each subcommittee, for the slot before the block's and its
    /// parent root (altair/validator.md "Sync committee").
    /// </remarks>
    internal SyncCommitteeContribution? GetBestContribution(ulong slot, Hash256 blockRoot, ulong subcommitteeIndex)
    {
        lock (_lock)
        {
            Prune(slotClock.CurrentSlot);
            return _bestContributions.GetValueOrDefault((slot, blockRoot, subcommitteeIndex));
        }
    }

    private static int CountParticipants(SyncCommitteeContribution contribution)
    {
        int count = 0;
        BitArray bits = contribution.AggregationBits!;
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i]) count++;
        }

        return count;
    }

    // Callers hold _lock.
    private void Prune(ulong currentSlot)
    {
        if (currentSlot == _prunedAtSlot) return;
        _prunedAtSlot = currentSlot;
        foreach (((ulong Slot, Hash256, ulong) key, Aggregate aggregate) in _messages)
        {
            if (key.Slot + 1 < currentSlot && _messages.Remove(key))
                _signatureCount -= aggregate.Signatures.Count;
        }

        foreach ((ulong Slot, Hash256, ulong) key in _bestContributions.Keys)
        {
            if (key.Slot + 1 < currentSlot) _bestContributions.Remove(key);
        }
    }

    private sealed class Aggregate
    {
        public BitArray Bits { get; } = new(SyncCommitteeContribution.SubcommitteeSize);
        public List<BlsSignature> Signatures { get; } = [];
    }
}
