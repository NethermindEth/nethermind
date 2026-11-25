// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Protocols.Pubsub;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;

namespace Nethermind.BeaconChain.P2P.Gossip;

/// <summary>
/// The electra/p2p-interface.md <c>beacon_attestation_{subnet_id}</c> rules for a <see cref="SingleAttestation"/>, checked against the
/// published head, for attestations received on a subnet and those submitted through the validator API alike.
/// </summary>
/// <remarks>
/// <para>
/// Every check reads one <see cref="HeadSnapshot"/>, whose post-state and fork-choice copy are never mutated after publication, so it runs
/// on the calling thread and never waits for the import worker. The committees and the signature domain come from the head state, as in the
/// spec; fork choice checks the vote again on the worker against its target state.
/// </para>
/// <para>
/// The rules run in the spec's order, so a REJECT is returned only once every IGNORE ordered before it passed. The committees are those of
/// the slot's epoch, which the target epoch must match anyway. An accepted attestation marks its (target epoch, attester) pair seen and is
/// added to <see cref="AttestationPool"/>.
/// </para>
/// </remarks>
public sealed class SingleAttestationValidator(
    BeaconChainSpec spec,
    SlotClock slotClock,
    HeadSnapshotHolder headSnapshots,
    PubkeyCache pubkeys,
    FailedBlockRoots failedBlocks,
    AttestationPool pool)
{
    /// <summary>The (target epoch, attester) pairs remembered: two epochs of two subnets with mainnet-sized committees of about 512; an evicted pair only has a later copy of its vote checked again.</summary>
    internal const int SeenAttesterCapacity = 1 << 16;

    private readonly CommitteeCacheLru _committees = new();
    private readonly LruKeyCache<(ulong TargetEpoch, ulong AttesterIndex)> _seenAttesters = new(SeenAttesterCapacity, "beacon gossip seen attesters");
    private volatile SnapshotNodes? _nodes;

    /// <summary>The committees of <paramref name="epoch"/> as <paramref name="state"/> shuffles them, built once for each shuffling.</summary>
    internal CommitteeCache GetCommittees(BeaconStateFulu state, ulong epoch)
    {
        lock (_committees) return _committees.GetOrBuild(state, epoch);
    }

    /// <summary>The slots the wall clock may be in, allowing <c>MAXIMUM_GOSSIP_CLOCK_DISPARITY</c> either way.</summary>
    internal (ulong Earliest, ulong Latest) CurrentSlots()
    {
        ulong currentSlot = slotClock.CurrentSlot;
        long now = slotClock.UnixMilliseconds;
        ulong latest = now + GossipRouter.MaximumGossipClockDisparityMs >= slotClock.SlotStartMilliseconds(currentSlot + 1) ? currentSlot + 1 : currentSlot;
        ulong earliest = currentSlot > 0 && now - GossipRouter.MaximumGossipClockDisparityMs <= slotClock.SlotStartMilliseconds(currentSlot) ? currentSlot - 1 : currentSlot;
        return (earliest, latest);
    }

    /// <summary>Checks <paramref name="attestation"/> and, when it passes, marks its attester seen for the target epoch and adds it to the pool.</summary>
    /// <param name="subnet">
    /// The subnet a gossip attestation arrived on, or <c>null</c> for one submitted locally. A local one skips the already-seen IGNORE, so a
    /// validator's own vote is accepted however the node saw it before, and the subnet REJECT: its subnet is computed instead.
    /// </param>
    internal AttestationCheck Check(SingleAttestation attestation, ulong? subnet)
    {
        if (attestation.Data is not { BeaconBlockRoot: { } blockRoot, Source: not null, Target: { Root: { } targetRoot } target } data)
            return AttestationCheck.Reject(GossipDropReason.InvalidField, "The attestation has no data.");

        (ulong, ulong) seenKey = (target.Epoch, attestation.AttesterIndex);
        if (subnet is not null && _seenAttesters.Get(seenKey))
            return AttestationCheck.Ignore(GossipDropReason.Duplicate, $"An attestation of validator {attestation.AttesterIndex} for target epoch {target.Epoch} was already seen.");
        if (data.Index != 0) return AttestationCheck.Reject(GossipDropReason.InvalidField, "The attestation data index is not zero.");
        if (headSnapshots.Current is not { ForkChoice: { } forkChoice, State: ForkedBeaconState.OfFulu { State: { } state } })
            return AttestationCheck.Ignore(GossipDropReason.Unverifiable, "The head state is not captured yet.");

        Dictionary<Hash256, ForkChoiceSnapshotNode> nodes = NodesOf(forkChoice);
        if (!nodes.ContainsKey(blockRoot))
            return failedBlocks.Contains(blockRoot)
                ? AttestationCheck.Reject(GossipDropReason.InvalidField, $"Block {blockRoot} failed validation.")
                : AttestationCheck.Ignore(GossipDropReason.UnknownBlock, $"Block {blockRoot} is not known.");

        ulong epoch = spec.GetEpoch(data.Slot);
        ulong headEpoch = state.GetCurrentEpoch();
        // MIN_SEED_LOOKAHEAD: a state knows the shufflings of its previous, current and next epochs.
        if (epoch > headEpoch + 1 || epoch + 1 < headEpoch)
            return AttestationCheck.Ignore(GossipDropReason.Unverifiable, $"The head at epoch {headEpoch} does not know the committees of epoch {epoch}.");

        CommitteeCache committees = GetCommittees(state, epoch);
        if (attestation.CommitteeIndex >= (ulong)committees.CommitteesPerSlot)
            return AttestationCheck.Reject(GossipDropReason.InvalidField, $"Committee index {attestation.CommitteeIndex} is out of range ({committees.CommitteesPerSlot} committees per slot).");
        ulong expectedSubnet = GossipTopics.ComputeSubnetForAttestation((ulong)committees.CommitteesPerSlot, data.Slot, attestation.CommitteeIndex);
        if (subnet is { } receivedOn && receivedOn != expectedSubnet)
            return AttestationCheck.Reject(GossipDropReason.InvalidField, $"The attestation belongs on subnet {expectedSubnet}, not {receivedOn}.");

        (ulong earliestSlot, ulong latestSlot) = CurrentSlots();
        if (data.Slot > latestSlot) return AttestationCheck.Ignore(GossipDropReason.FutureSlot, $"Slot {data.Slot} is in the future.");
        if (epoch + 1 < spec.GetEpoch(earliestSlot)) return AttestationCheck.Ignore(GossipDropReason.StaleSlot, $"Epoch {epoch} is neither the current nor the previous epoch.");
        if (target.Epoch != epoch) return AttestationCheck.Reject(GossipDropReason.InvalidField, $"Target epoch {target.Epoch} is not the epoch {epoch} of the slot.");

        ReadOnlySpan<int> committee = committees.GetBeaconCommittee(data.Slot, (int)attestation.CommitteeIndex);
        int position = attestation.AttesterIndex < int.MaxValue ? committee.IndexOf((int)attestation.AttesterIndex) : -1;
        if (position < 0)
            return AttestationCheck.Reject(GossipDropReason.InvalidField, $"Validator {attestation.AttesterIndex} is not in committee {attestation.CommitteeIndex} at slot {data.Slot}.");

        Hash256 dataRoot = SszRoots.HashTreeRoot(data);
        if (!IsSignatureValid(state, attestation.AttesterIndex, dataRoot, target.Epoch, attestation.Signature))
            return AttestationCheck.Reject(GossipDropReason.InvalidSignature, "The signature is invalid.");
        if (CheckpointBlock(nodes, blockRoot, epoch) != targetRoot)
            return AttestationCheck.Reject(GossipDropReason.InvalidField, $"Target {targetRoot} is not the checkpoint block of {blockRoot}.");
        CheckpointRef finalized = forkChoice.FinalizedCheckpoint;
        if (CheckpointBlock(nodes, blockRoot, finalized.Epoch) != finalized.Root)
            return AttestationCheck.Ignore(GossipDropReason.NotFinalizedDescendant, $"Block {blockRoot} does not descend from the finalized checkpoint {finalized}.");

        // Set is false when a concurrent copy of the vote was accepted first.
        if (!_seenAttesters.Set(seenKey) && subnet is not null)
            return AttestationCheck.Ignore(GossipDropReason.Duplicate, $"An attestation of validator {attestation.AttesterIndex} for target epoch {target.Epoch} was already seen.");

        pool.Add(data, dataRoot, attestation.CommitteeIndex, committee.Length, position, attestation.Signature);
        return new AttestationCheck(MessageValidity.Accepted, default, null, expectedSubnet, new Attestation
        {
            AggregationBits = new BitArray(committee.Length) { [position] = true },
            Data = data,
            Signature = attestation.Signature,
            CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [(int)attestation.CommitteeIndex] = true },
        });
    }

    private bool IsSignatureValid(BeaconStateFulu state, ulong validatorIndex, Hash256 dataRoot, ulong targetEpoch, BlsSignature signature)
    {
        if (!SignatureSets.TryGetValidatorKey(pubkeys, state.ReadOnlyValidators, validatorIndex, out G1Affine key)) return false;
        Hash256 domain = state.GetDomain(DomainType.BeaconAttester, targetEpoch);
        return BlockSignatureBatch.Verify(key, signature, Domains.ComputeSigningRoot(dataRoot, domain), null);
    }

    private Dictionary<Hash256, ForkChoiceSnapshotNode> NodesOf(ForkChoiceSnapshot forkChoice)
    {
        SnapshotNodes? indexed = _nodes;
        if (!ReferenceEquals(indexed?.Snapshot, forkChoice))
        {
            Dictionary<Hash256, ForkChoiceSnapshotNode> nodes = [with(forkChoice.Nodes.Count)];
            foreach (ForkChoiceSnapshotNode node in forkChoice.Nodes) nodes[node.Root] = node;
            _nodes = indexed = new SnapshotNodes(forkChoice, nodes);
        }

        return indexed!.Nodes;
    }

    /// <summary>fork-choice.md <c>get_checkpoint_block</c> over the snapshot: the latest block of <paramref name="blockRoot"/>'s chain at or before the first slot of <paramref name="epoch"/>.</summary>
    /// <returns><c>null</c> when that block is below the snapshot.</returns>
    private static Hash256? CheckpointBlock(Dictionary<Hash256, ForkChoiceSnapshotNode> nodes, Hash256 blockRoot, ulong epoch)
    {
        ulong startSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch);
        for (ForkChoiceSnapshotNode? node = nodes.GetValueOrDefault(blockRoot); node is not null; node = node.ParentRoot is { } parent ? nodes.GetValueOrDefault(parent) : null)
        {
            if (node.Slot <= startSlot) return node.Root;
        }

        return null;
    }

    private sealed record SnapshotNodes(ForkChoiceSnapshot Snapshot, Dictionary<Hash256, ForkChoiceSnapshotNode> Nodes);
}

/// <summary>The outcome of <see cref="SingleAttestationValidator.Check"/>.</summary>
/// <param name="Reason">Why an attestation that is not accepted was dropped.</param>
/// <param name="Refusal">What refused an attestation that is not accepted, for the one who submitted it.</param>
/// <param name="Subnet">The subnet an accepted attestation is published on.</param>
/// <param name="Vote">An accepted attestation in the form fork choice takes.</param>
internal readonly record struct AttestationCheck(MessageValidity Validity, GossipDropReason Reason, string? Refusal, ulong Subnet, Attestation? Vote)
{
    public static AttestationCheck Reject(GossipDropReason reason, string refusal) => new(MessageValidity.Rejected, reason, refusal, 0, null);
    public static AttestationCheck Ignore(GossipDropReason reason, string refusal) => new(MessageValidity.Ignored, reason, refusal, 0, null);
}
