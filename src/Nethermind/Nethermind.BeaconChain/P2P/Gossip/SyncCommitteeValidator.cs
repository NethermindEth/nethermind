// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Runtime.CompilerServices;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Libp2p.Protocols.Pubsub;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>
/// The altair/p2p-interface.md <c>sync_committee_{subnet_id}</c> and <c>sync_committee_contribution_and_proof</c> rules, checked against the
/// published head, for messages and contributions received on gossip and those submitted through the validator API alike.
/// </summary>
/// <remarks>
/// <para>
/// Every check reads one <see cref="HeadSnapshot"/>, whose post-state is never mutated after publication, so it runs on the calling thread and
/// never waits for the import worker; nothing here feeds fork choice. The rules run in the spec's order, so a REJECT is returned only once every
/// IGNORE ordered before it passed. What passes is marked seen and added to the <see cref="SyncCommitteePool"/>.
/// </para>
/// <para>
/// The committee checked against is the one that signs for the message's slot, that of the sync committee period of the next slot
/// (altair/validator.md "Sync Committee"), read from the head state's current or next committee. <c>compute_subnets_for_sync_committee</c>
/// and <c>get_sync_subcommittee_pubkeys</c> choose it by the head state's slot instead, which is the same while the head is at the message's
/// slot; choosing by the message keeps a head a slot behind across a period boundary from checking against the committee that no longer signs.
/// </para>
/// <para>
/// Signatures are checked against the committee's own keys, decompressed and <c>KeyValidate</c>d once for each committee, the first time one
/// of its signatures is checked.
/// </para>
/// <para>Thread safety: every member may be called from any thread and concurrently.</para>
/// </remarks>
public sealed class SyncCommitteeValidator(BeaconChainSpec spec, SlotClock slotClock, HeadSnapshotHolder headSnapshots, SyncCommitteePool pool)
{
    /// <summary>The (slot, validator, subnet) triples of accepted messages remembered: every position of two slots, twice over; an evicted one only has a later copy checked again.</summary>
    internal const int SeenMessageCapacity = 4 * Presets.SyncCommitteeSize;

    /// <summary>The (slot, aggregator, subcommittee) triples of accepted contributions remembered: every position of two slots, twice over.</summary>
    internal const int SeenAggregatorCapacity = 4 * Presets.SyncCommitteeSize;

    private const int SeenBitsPerContributionData = (int)Presets.TargetAggregatorsPerSyncSubcommittee;

    private readonly LruKeyCache<(ulong Slot, ulong ValidatorIndex, ulong Subnet)> _seenMessages = new(SeenMessageCapacity, "beacon gossip seen sync committee messages");
    private readonly LruKeyCache<(ulong Slot, ulong AggregatorIndex, ulong SubcommitteeIndex)> _seenAggregators = new(SeenAggregatorCapacity, "beacon gossip seen sync committee aggregators");
    private readonly Lock _seenContributionBitsLock = new();
    private readonly LruCache<(ulong Slot, Hash256 BlockRoot, ulong SubcommitteeIndex), List<BitArray>> _seenContributionBits =
        new(SyncCommitteePool.MaxContributions, "beacon gossip seen sync committee contribution bits");
    private readonly ConditionalWeakTable<SyncCommittee, CommitteeKeys> _committees = [];

    /// <summary>Checks <paramref name="message"/> and, when it passes, marks it seen and adds it to the pool.</summary>
    /// <param name="subnet">
    /// The subnet a gossip message arrived on, or <c>null</c> for one submitted locally. A local one skips the already-seen IGNORE, so a
    /// validator's own message is accepted however the node saw it before, and is checked for every subnet of its validator instead of one.
    /// </param>
    internal SyncCommitteeCheck CheckMessage(SyncCommitteeMessage message, ulong? subnet)
    {
        if (message.BeaconBlockRoot is not { } blockRoot)
            return SyncCommitteeCheck.Reject(GossipDropReason.InvalidField, "The message has no block root.");
        if (subnet is { } receivedOn && _seenMessages.Get((message.Slot, message.ValidatorIndex, receivedOn)))
            return SyncCommitteeCheck.Ignore(GossipDropReason.Duplicate, $"A message of validator {message.ValidatorIndex} for slot {message.Slot} was already seen on subnet {receivedOn}.");
        if (CheckCurrentSlot(message.Slot) is { } timing) return timing;
        if (HeadState() is not { } state) return NotCaptured;

        ReadOnlySpan<Validator> validators = state.ReadOnlyValidators;
        if (message.ValidatorIndex >= (ulong)validators.Length)
            return SyncCommitteeCheck.Reject(GossipDropReason.InvalidField, $"Validator index {message.ValidatorIndex} is out of range.");
        if (Committee(state, message.Slot) is not { } committee) return UnknownCommittee(state, message.Slot);

        int[] positions = committee.PositionsOf(validators[(int)message.ValidatorIndex].Pubkey);
        ulong validSubnets = SubnetsOf(positions);
        ulong subnets = subnet is { } on ? 1UL << (int)on : validSubnets;
        if (subnets == 0 || (subnets & validSubnets) != subnets)
            return SyncCommitteeCheck.Reject(GossipDropReason.InvalidField, $"Validator {message.ValidatorIndex} is not in the sync {(subnet is null ? "committee" : $"subcommittee {subnet}")} of slot {message.Slot}.");

        Hash256 domain = state.GetDomain(DomainType.SyncCommittee, spec.GetEpoch(message.Slot));
        if (!committee.TryGetKey(positions[0], out G1Affine key) || !Verify(key, message.Signature, blockRoot, domain))
            return SyncCommitteeCheck.Reject(GossipDropReason.InvalidSignature, "The signature is invalid.");

        for (ulong marked = 0; marked < SyncCommitteeContribution.SubnetCount; marked++)
        {
            // Set is false when a concurrent copy on the subnet was accepted first.
            if ((subnets >> (int)marked & 1) != 0 && !_seenMessages.Set((message.Slot, message.ValidatorIndex, marked)) && subnet is not null)
                return SyncCommitteeCheck.Ignore(GossipDropReason.Duplicate, $"A message of validator {message.ValidatorIndex} for slot {message.Slot} was already seen on subnet {marked}.");
        }

        for (ulong pooled = 0; pooled < SyncCommitteeContribution.SubnetCount; pooled++)
        {
            if ((subnets >> (int)pooled & 1) != 0) pool.AddMessage(message, pooled, PositionsWithin(positions, pooled));
        }

        return SyncCommitteeCheck.Accept(subnets);
    }

    /// <summary>Checks <paramref name="signed"/> and, when it passes, marks it seen and offers its contribution to the pool.</summary>
    /// <remarks>A contribution submitted locally is held to the already-seen IGNOREs too: one the network already has is not published again.</remarks>
    internal SyncCommitteeCheck CheckContribution(SignedContributionAndProof signed)
    {
        if (signed.Message is not { Contribution: { BeaconBlockRoot: { } blockRoot, AggregationBits: { Length: SyncCommitteeContribution.SubcommitteeSize } bits } contribution } message)
            return SyncCommitteeCheck.Reject(GossipDropReason.InvalidField, "The contribution has no block root or aggregation bits.");

        ulong slot = contribution.Slot;
        ulong subcommitteeIndex = contribution.SubcommitteeIndex;
        (ulong, Hash256, ulong) dataKey = (slot, blockRoot, subcommitteeIndex);
        lock (_seenContributionBitsLock)
        {
            if (_seenContributionBits.TryGet(dataKey, out List<BitArray>? seen) && GossipRouter.IsCovered(seen, bits))
                return SyncCommitteeCheck.Ignore(GossipDropReason.Duplicate, $"A contribution of subcommittee {subcommitteeIndex} for slot {slot} covering these participants was already seen.");
        }

        (ulong, ulong, ulong) aggregatorKey = (slot, message.AggregatorIndex, subcommitteeIndex);
        if (_seenAggregators.Get(aggregatorKey))
            return SyncCommitteeCheck.Ignore(GossipDropReason.Duplicate, $"A contribution of aggregator {message.AggregatorIndex} for subcommittee {subcommitteeIndex} at slot {slot} was already seen.");
        if (CheckCurrentSlot(slot) is { } timing) return timing;
        if (subcommitteeIndex >= SyncCommitteeContribution.SubnetCount)
            return SyncCommitteeCheck.Reject(GossipDropReason.InvalidField, $"Subcommittee index {subcommitteeIndex} is out of range.");
        if (!bits.HasAnySet()) return SyncCommitteeCheck.Reject(GossipDropReason.InvalidField, "The contribution has no participants.");
        if (!BeaconStateAccessors.IsSyncCommitteeAggregator(message.SelectionProof))
            return SyncCommitteeCheck.Reject(GossipDropReason.InvalidField, $"The selection proof does not select validator {message.AggregatorIndex} as an aggregator.");
        if (HeadState() is not { } state) return NotCaptured;

        ReadOnlySpan<Validator> validators = state.ReadOnlyValidators;
        if (message.AggregatorIndex >= (ulong)validators.Length)
            return SyncCommitteeCheck.Reject(GossipDropReason.InvalidField, $"Aggregator index {message.AggregatorIndex} is out of range.");
        if (Committee(state, slot) is not { } committee) return UnknownCommittee(state, slot);

        List<int> aggregatorPositions = PositionsWithin(committee.PositionsOf(validators[(int)message.AggregatorIndex].Pubkey), subcommitteeIndex);
        if (aggregatorPositions.Count == 0)
            return SyncCommitteeCheck.Reject(GossipDropReason.InvalidField, $"Validator {message.AggregatorIndex} is not in sync subcommittee {subcommitteeIndex} of slot {slot}.");

        int firstPosition = (int)subcommitteeIndex * SyncCommitteeContribution.SubcommitteeSize;
        ulong epoch = spec.GetEpoch(slot);
        if (!committee.TryGetKey(firstPosition + aggregatorPositions[0], out G1Affine aggregatorPublicKey)
            || !Verify(aggregatorPublicKey, message.SelectionProof, SszRoots.HashTreeRoot(new SyncAggregatorSelectionData { Slot = slot, SubcommitteeIndex = subcommitteeIndex }),
                state.GetDomain(DomainType.SyncCommitteeSelectionProof, epoch)))
            return SyncCommitteeCheck.Reject(GossipDropReason.InvalidSignature, "The selection proof is invalid.");
        if (!Verify(aggregatorPublicKey, signed.Signature, SszRoots.HashTreeRoot(message), state.GetDomain(DomainType.ContributionAndProof, epoch)))
            return SyncCommitteeCheck.Reject(GossipDropReason.InvalidSignature, "The aggregator signature is invalid.");
        if (!IsAggregateValid(committee, firstPosition, bits, contribution.Signature, Domains.ComputeSigningRoot(blockRoot, state.GetDomain(DomainType.SyncCommittee, epoch))))
            return SyncCommitteeCheck.Reject(GossipDropReason.InvalidSignature, "The aggregate signature is invalid.");

        // Set is false when a concurrent copy was accepted first.
        if (!_seenAggregators.Set(aggregatorKey))
            return SyncCommitteeCheck.Ignore(GossipDropReason.Duplicate, $"A contribution of aggregator {message.AggregatorIndex} for subcommittee {subcommitteeIndex} at slot {slot} was already seen.");
        lock (_seenContributionBitsLock)
        {
            if (!_seenContributionBits.TryGet(dataKey, out List<BitArray>? seen))
            {
                seen = [];
                _seenContributionBits.Set(dataKey, seen);
            }

            GossipRouter.AddSeenBits(seen, bits, SeenBitsPerContributionData);
        }

        pool.AddContribution(contribution);
        return SyncCommitteeCheck.Accept(0);
    }

    private static SyncCommitteeCheck NotCaptured => SyncCommitteeCheck.Ignore(GossipDropReason.Unverifiable, "The head state is not captured yet.");

    private static SyncCommitteeCheck UnknownCommittee(BeaconStateFulu state, ulong slot) =>
        SyncCommitteeCheck.Ignore(GossipDropReason.Unverifiable, $"The head at epoch {state.GetCurrentEpoch()} does not know the sync committee of slot {slot}.");

    private SyncCommitteeCheck? CheckCurrentSlot(ulong slot) => GossipRouter.CurrentSlotRefusal(slotClock, slot) switch
    {
        GossipDropReason.FutureSlot => SyncCommitteeCheck.Ignore(GossipDropReason.FutureSlot, $"Slot {slot} is in the future."),
        { } stale => SyncCommitteeCheck.Ignore(stale, $"Slot {slot} has passed."),
        null => null,
    };

    private BeaconStateFulu? HeadState() => headSnapshots.Current?.State is ForkedBeaconState.OfFulu { State: { } state } ? state : null;

    /// <summary>The keys of the committee that signs for <paramref name="slot"/> as the head state holds it, or <c>null</c> when it holds neither period's.</summary>
    private CommitteeKeys? Committee(BeaconStateFulu state, ulong slot)
    {
        ulong period = spec.GetEpoch(slot + 1) / Presets.EpochsPerSyncCommitteePeriod;
        ulong headPeriod = state.GetCurrentEpoch() / Presets.EpochsPerSyncCommitteePeriod;
        SyncCommittee? committee = period == headPeriod ? state.CurrentSyncCommittee : period == headPeriod + 1 ? state.NextSyncCommittee : null;
        return committee is null ? null : _committees.GetValue(committee, static added => new CommitteeKeys(added.Pubkeys!));
    }

    /// <summary>One bit for each subnet whose subcommittee holds any of <paramref name="positions"/>, subnet 0 lowest.</summary>
    private static ulong SubnetsOf(int[] positions)
    {
        ulong subnets = 0;
        foreach (int position in positions) subnets |= 1UL << (position / SyncCommitteeContribution.SubcommitteeSize);
        return subnets;
    }

    /// <summary>The positions within subcommittee <paramref name="subnet"/> of those of <paramref name="positions"/> in it.</summary>
    private static List<int> PositionsWithin(int[] positions, ulong subnet)
    {
        List<int> within = [];
        foreach (int position in positions)
        {
            if ((ulong)(position / SyncCommitteeContribution.SubcommitteeSize) == subnet) within.Add(position % SyncCommitteeContribution.SubcommitteeSize);
        }

        return within;
    }

    /// <summary><c>eth_fast_aggregate_verify</c> of <paramref name="signature"/> by the members of the subcommittee starting at <paramref name="firstPosition"/> whose bit is set.</summary>
    /// <remarks>A member holding several positions counts once for each of its bits; participants whose keys sum to infinity are refused.</remarks>
    private static bool IsAggregateValid(CommitteeKeys committee, int firstPosition, BitArray bits, BlsSignature signature, Hash256 signingRoot)
    {
        Bls.P1 participants = new(stackalloc long[Bls.P1.Sz]);
        for (int i = 0; i < bits.Length; i++)
        {
            if (!bits[i]) continue;
            if (!committee.TryGetKey(firstPosition + i, out G1Affine member)) return false;
            participants.Add(member);
        }

        return BlockSignatureBatch.Verify(participants.ToAffine(), signature, signingRoot, null);
    }

    private static bool Verify(G1Affine key, BlsSignature signature, Hash256 objectRoot, Hash256 domain) =>
        BlockSignatureBatch.Verify(key, signature, Domains.ComputeSigningRoot(objectRoot, domain), null);

    /// <summary>The positions of each member of one sync committee, and its members' keys, decompressed on first use.</summary>
    private sealed class CommitteeKeys
    {
        private readonly BlsPublicKey[] _members;
        private readonly Dictionary<BlsPublicKey, int[]> _positions = [];
        private readonly Lazy<(long[] Points, bool[] Valid)> _keys;

        public CommitteeKeys(BlsPublicKey[] members)
        {
            _members = members;
            Dictionary<BlsPublicKey, List<int>> positions = [];
            for (int position = 0; position < members.Length; position++)
            {
                if (!positions.TryGetValue(members[position], out List<int>? held))
                    positions[members[position]] = held = [];
                held.Add(position);
            }

            foreach ((BlsPublicKey member, List<int> held) in positions) _positions[member] = [.. held];
            _keys = new(Decompress);
        }

        /// <summary>The committee positions <paramref name="member"/> holds, in order; empty when it holds none.</summary>
        public int[] PositionsOf(BlsPublicKey member) => _positions.GetValueOrDefault(member, []);

        /// <summary>The key of the member at <paramref name="position"/>.</summary>
        /// <returns><c>false</c> when the key fails <c>KeyValidate</c>.</returns>
        public bool TryGetKey(int position, out G1Affine key)
        {
            (long[] points, bool[] valid) = _keys.Value;
            key = new G1Affine(points.AsSpan(position * G1Affine.Sz, G1Affine.Sz));
            return valid[position];
        }

        private (long[] Points, bool[] Valid) Decompress()
        {
            long[] points = new long[_members.Length * G1Affine.Sz];
            bool[] valid = new bool[_members.Length];
            for (int position = 0; position < _members.Length; position++)
            {
                valid[position] = BlsSignatureSet.TryKeyValidate(_members[position].Bytes, new G1Affine(points.AsSpan(position * G1Affine.Sz, G1Affine.Sz)));
            }

            return (points, valid);
        }
    }
}

/// <summary>The outcome of a <see cref="SyncCommitteeValidator"/> check.</summary>
/// <param name="Reason">Why a message or contribution that is not accepted was dropped.</param>
/// <param name="Refusal">What refused a message or contribution that is not accepted, for the one who submitted it.</param>
/// <param name="Subnets">For an accepted sync committee message, one bit for each subnet it is published on, subnet 0 lowest.</param>
internal readonly record struct SyncCommitteeCheck(MessageValidity Validity, GossipDropReason Reason, string? Refusal, ulong Subnets)
{
    public static SyncCommitteeCheck Accept(ulong subnets) => new(MessageValidity.Accepted, default, null, subnets);
    public static SyncCommitteeCheck Reject(GossipDropReason reason, string refusal) => new(MessageValidity.Rejected, reason, refusal, 0);
    public static SyncCommitteeCheck Ignore(GossipDropReason reason, string refusal) => new(MessageValidity.Ignored, reason, refusal, 0);
}
