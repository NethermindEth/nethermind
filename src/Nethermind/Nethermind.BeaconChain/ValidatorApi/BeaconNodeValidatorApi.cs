// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Runtime.CompilerServices;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Snappier;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;

namespace Nethermind.BeaconChain.ValidatorApi;

/// <summary>Serves <see cref="IBeaconNodeValidatorApi"/> from the head the import worker last published.</summary>
/// <remarks>
/// <para>
/// Every call reads one <see cref="HeadSnapshot"/>, whose post-state and fork-choice copy are never mutated after publication,
/// so no call waits for the import worker. A submitted attestation is checked as gossip against that snapshot on the calling
/// thread, published, and only then queued to the worker for fork choice.
/// </para>
/// <para>
/// The worker captures head states only while <see cref="HeadSnapshotHolder.CaptureState"/> is set. The first call that needs
/// one sets it, so a node without a validator client pays nothing, and that call is refused as not ready.
/// </para>
/// <para>Only the attest path is served; a Gloas head or slot is refused as not supported.</para>
/// </remarks>
public sealed class BeaconNodeValidatorApi(
    BeaconChainSpec spec,
    SlotClock slotClock,
    HeadSnapshotHolder headSnapshots,
    PubkeyCache pubkeys,
    IEngineDriver engine,
    BeaconSyncOrchestrator orchestrator,
    BeaconP2P p2p) : IBeaconNodeValidatorApi
{
    /// <summary>Slots the head may lag the wall clock by and still count as synced, as <c>/eth/v1/node/syncing</c> counts them.</summary>
    private const ulong ReadySyncDistance = 1;

    /// <summary>phase0/p2p-interface.md <c>ATTESTATION_SUBNET_COUNT</c>.</summary>
    private const ulong AttestationSubnetCount = 64;

    // Keep this separate from the import worker's non-thread-safe cache; concurrent API requests lock this instance.
    private readonly CommitteeCacheLru _committees = new();
    private readonly ConditionalWeakTable<BeaconStateFulu, Lazy<Checkpoint>> _unrealizedJustification = [];

    /// <summary>Publishes a gossip message on a topic, subscribed or not; tests script it, otherwise <see cref="BeaconP2P.Publish"/>.</summary>
    internal Action<string, byte[]> PublishGossip { get; set; } = p2p.Publish;

    /// <inheritdoc/>
    public ValueTask<SyncingStatus> GetSyncingStatus(CancellationToken cancellationToken) => new(Status(headSnapshots.Current));

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<ValidatorSummary>> GetValidators(IReadOnlyList<BlsPublicKey> publicKeys, CancellationToken cancellationToken)
    {
        ReadOnlySpan<Validator> validators = RequireHeadState().State.ReadOnlyValidators;
        HashSet<BlsPublicKey> wanted = [.. publicKeys];
        List<ValidatorSummary> found = [];
        for (int index = 0; index < validators.Length && found.Count < wanted.Count; index++)
        {
            Validator validator = validators[index];
            if (wanted.Contains(validator.Pubkey))
                found.Add(new ValidatorSummary(validator.Pubkey, (ulong)index, validator.ActivationEpoch, validator.ExitEpoch, validator.Slashed));
        }

        return new(found);
    }

    /// <inheritdoc/>
    public ValueTask<Hash256> GetHeadBlockRoot(CancellationToken cancellationToken) =>
        new((headSnapshots.Current ?? throw NotReady("The node has no head yet.")).Status.HeadRoot!);

    /// <inheritdoc/>
    public ValueTask<DutiesResult<AttesterDuty>> GetAttesterDuties(ulong epoch, IReadOnlyList<ulong> validatorIndices, CancellationToken cancellationToken)
    {
        RequireDutyEpoch(epoch);
        (HeadSnapshot head, BeaconStateFulu state) = RequireHeadState();
        ulong headEpoch = state.GetCurrentEpoch();
        // MIN_SEED_LOOKAHEAD: a state knows the shufflings of its previous, current and next epochs.
        if (epoch > headEpoch + 1) throw NotReady($"The head at epoch {headEpoch} is too far behind to know the committees of epoch {epoch}.");
        if (epoch + 1 < headEpoch) throw InvalidRequest($"Epoch {epoch} is before the previous epoch of the head at epoch {headEpoch}.");

        CommitteeCache committees = GetCommittees(state, epoch);
        ReadOnlySpan<Validator> validators = state.ReadOnlyValidators;
        HashSet<ulong> wanted = [.. validatorIndices];
        List<AttesterDuty> duties = [];
        ulong startSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch);
        for (ulong slot = startSlot; slot < startSlot + Presets.SlotsPerEpoch; slot++)
        {
            for (int committeeIndex = 0; committeeIndex < committees.CommitteesPerSlot; committeeIndex++)
            {
                ReadOnlySpan<int> committee = committees.GetBeaconCommittee(slot, committeeIndex);
                for (int position = 0; position < committee.Length; position++)
                {
                    int validatorIndex = committee[position];
                    if (wanted.Contains((ulong)validatorIndex))
                        duties.Add(new AttesterDuty(validators[validatorIndex].Pubkey, (ulong)validatorIndex, (ulong)committeeIndex, (ulong)committee.Length,
                            (ulong)committees.CommitteesPerSlot, (ulong)position, slot));
                }
            }
        }

        return new(new DutiesResult<AttesterDuty>(DependentRoot(head, state, epoch), !head.ExecutionInSync, duties));
    }

    /// <inheritdoc/>
    public ValueTask<DutiesResult<ProposerDuty>> GetProposerDuties(ulong epoch, CancellationToken cancellationToken)
    {
        RequireDutyEpoch(epoch);
        (HeadSnapshot head, BeaconStateFulu state) = RequireHeadState();
        // EIP-7917: proposer_lookahead holds the proposers of the state's current and next epochs.
        ulong headEpoch = state.GetCurrentEpoch();
        if (epoch > headEpoch + 1) throw NotReady($"The head at epoch {headEpoch} is too far behind to know the proposers of epoch {epoch}.");
        if (epoch < headEpoch) throw InvalidRequest($"The proposer lookahead of the head at epoch {headEpoch} no longer holds epoch {epoch}.");

        ReadOnlySpan<Validator> validators = state.ReadOnlyValidators;
        ReadOnlySpan<ulong> proposers = state.ProposerLookahead.AsSpan((int)((epoch - headEpoch) * Presets.SlotsPerEpoch), (int)Presets.SlotsPerEpoch);
        ulong startSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch);
        ProposerDuty[] duties = new ProposerDuty[proposers.Length];
        for (int i = 0; i < duties.Length; i++)
        {
            duties[i] = new ProposerDuty(validators[(int)proposers[i]].Pubkey, proposers[i], startSlot + (ulong)i);
        }

        return new(new DutiesResult<ProposerDuty>(DependentRoot(head, state, epoch), !head.ExecutionInSync, duties));
    }

    /// <inheritdoc/>
    public ValueTask<DutiesResult<SyncDuty>> GetSyncDuties(ulong epoch, IReadOnlyList<ulong> validatorIndices, CancellationToken cancellationToken)
    {
        ulong period = epoch / Presets.EpochsPerSyncCommitteePeriod;
        ulong currentPeriod = slotClock.CurrentEpoch / Presets.EpochsPerSyncCommitteePeriod;
        if (period > currentPeriod + 1) throw InvalidRequest($"Epoch {epoch} is in sync-committee period {period}, after the next period {currentPeriod + 1}.");
        RefuseGloas(epoch);
        (HeadSnapshot head, BeaconStateFulu state) = RequireHeadState();
        ulong headPeriod = state.GetCurrentEpoch() / Presets.EpochsPerSyncCommitteePeriod;
        SyncCommittee committee = period == headPeriod ? state.CurrentSyncCommittee!
            : period == headPeriod + 1 ? state.NextSyncCommittee!
            : period > headPeriod ? throw NotReady($"The head in sync-committee period {headPeriod} is too far behind to know period {period}.")
            : throw InvalidRequest($"Sync-committee period {period} is before the period {headPeriod} of the head.");

        Dictionary<BlsPublicKey, List<ulong>> positions = [];
        BlsPublicKey[] members = committee.Pubkeys!;
        for (int position = 0; position < members.Length; position++)
        {
            if (!positions.TryGetValue(members[position], out List<ulong>? held))
                positions[members[position]] = held = [];
            held.Add((ulong)position);
        }

        ReadOnlySpan<Validator> validators = state.ReadOnlyValidators;
        List<SyncDuty> duties = [];
        foreach (ulong validatorIndex in validatorIndices)
        {
            if (validatorIndex < (ulong)validators.Length && positions.TryGetValue(validators[(int)validatorIndex].Pubkey, out List<ulong>? held))
                duties.Add(new SyncDuty(validators[(int)validatorIndex].Pubkey, validatorIndex, held));
        }

        return new(new DutiesResult<SyncDuty>(null, !head.ExecutionInSync, duties));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// phase0/validator.md "Attestation data" reads the head state advanced to <paramref name="slot"/>. Within the head's epoch
    /// advancing changes neither checkpoint; across one boundary, <c>process_justification_and_finalization</c> runs once on the
    /// head's participation, which is the source computed here. A head further behind is refused rather than advanced.
    /// </remarks>
    public ValueTask<AttestationData> ProduceAttestationData(ulong slot, ulong committeeIndex, CancellationToken cancellationToken)
    {
        ulong epoch = spec.GetEpoch(slot);
        RefuseGloas(epoch);
        (ulong earliestSlot, ulong latestSlot) = CurrentSlots();
        if (slot < earliestSlot || slot > latestSlot) throw InvalidRequest($"Slot {slot} is not the current slot {slotClock.CurrentSlot}.");
        (HeadSnapshot head, BeaconStateFulu state) = RequireHeadState();
        if (!head.ExecutionInSync) throw NotReady("The head's execution payload is not yet verified.");
        if (slot < head.Status.HeadSlot) throw InvalidRequest($"Slot {slot} is before the head at slot {head.Status.HeadSlot}.");

        ulong headEpoch = state.GetCurrentEpoch();
        Checkpoint source = epoch == headEpoch ? state.CurrentJustifiedCheckpoint!
            : epoch == headEpoch + 1 ? _unrealizedJustification.GetValue(state,
                static snapshot => new(() => EpochProcessing.ComputeJustificationAndFinalization(snapshot, new EpochCache()).CurrentJustifiedCheckpoint)).Value
            : throw NotReady($"The head state at epoch {headEpoch} is not advanced to epoch {epoch}.");
        return new(new AttestationData
        {
            Slot = slot,
            BeaconBlockRoot = head.Status.HeadRoot,
            Source = new Checkpoint { Epoch = source.Epoch, Root = source.Root },
            Target = new Checkpoint { Epoch = epoch, Root = BlockRootAtSlot(head, state, BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch)) },
        });
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Applies the electra/p2p-interface.md <c>beacon_attestation_{subnet_id}</c> checks against the head snapshot, except the
    /// per-validator "already seen" IGNORE, which guards relays rather than a node's own validators. Fork choice checks the vote
    /// again on the worker against its target state, so a vote whose committee differs there is not counted.
    /// </remarks>
    public ValueTask<IReadOnlyList<SubmissionFailure>> SubmitAttestations(IReadOnlyList<SingleAttestation> attestations, CancellationToken cancellationToken)
    {
        foreach (SingleAttestation attestation in attestations)
        {
            if (attestation.Data is { } data) RefuseGloas(spec.GetEpoch(data.Slot));
        }

        (HeadSnapshot head, BeaconStateFulu state) = RequireHeadState();
        ForkChoiceSnapshot forkChoice = head.ForkChoice ?? throw NotReady("The node has no fork-choice view yet.");
        Dictionary<Hash256, ForkChoiceSnapshotNode> nodes = [with(forkChoice.Nodes.Count)];
        foreach (ForkChoiceSnapshotNode node in forkChoice.Nodes) nodes[node.Root] = node;

        (ulong earliestSlot, ulong latestSlot) = CurrentSlots();
        List<SubmissionFailure> failures = [];
        for (int i = 0; i < attestations.Count; i++)
        {
            SingleAttestation attestation = attestations[i];
            string? refusal = Validate(attestation, state, forkChoice.FinalizedCheckpoint, nodes, earliestSlot, latestSlot, out ulong subnet, out Attestation? vote)
                ?? Publish(attestation, subnet);
            if (refusal is null)
                orchestrator.QueueGossipAttestation(vote!);
            else
                failures.Add(new SubmissionFailure(i, refusal));
        }

        return new(failures);
    }

    /// <inheritdoc/>
    public ValueTask SubscribeToBeaconCommittees(IReadOnlyList<BeaconCommitteeSubscription> subscriptions, CancellationToken cancellationToken) =>
        throw NotServed("POST /eth/v1/validator/beacon_committee_subscriptions");

    /// <inheritdoc/>
    public ValueTask<Attestation> GetAggregateAttestation(ulong slot, Hash256 attestationDataRoot, ulong committeeIndex, CancellationToken cancellationToken) =>
        throw NotServed("GET /eth/v2/validator/aggregate_attestation");

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SubmissionFailure>> SubmitAggregateAndProofs(IReadOnlyList<SignedAggregateAndProof> aggregates, CancellationToken cancellationToken) =>
        throw NotServed("POST /eth/v2/validator/aggregate_and_proofs");

    /// <inheritdoc/>
    public ValueTask PrepareBeaconProposers(IReadOnlyList<ProposerPreparation> preparations, CancellationToken cancellationToken) =>
        throw NotServed("POST /eth/v1/validator/prepare_beacon_proposer");

    /// <inheritdoc/>
    public ValueTask<ProducedBlock> ProduceBlock(ulong slot, BlsSignature randaoReveal, Hash256 graffiti, CancellationToken cancellationToken) =>
        throw NotServed("GET /eth/v3/validator/blocks/{slot}");

    /// <inheritdoc/>
    public ValueTask PublishBlock(SignedBlockContents contents, CancellationToken cancellationToken) =>
        throw NotServed("POST /eth/v2/beacon/blocks");

    /// <inheritdoc/>
    public ValueTask SubscribeToSyncCommittees(IReadOnlyList<SyncCommitteeSubscription> subscriptions, CancellationToken cancellationToken) =>
        throw NotServed("POST /eth/v1/validator/sync_committee_subscriptions");

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SubmissionFailure>> SubmitSyncCommitteeMessages(IReadOnlyList<SyncCommitteeMessage> messages, CancellationToken cancellationToken) =>
        throw NotServed("POST /eth/v1/beacon/pool/sync_committees");

    /// <inheritdoc/>
    public ValueTask<SyncCommitteeContribution> ProduceSyncCommitteeContribution(ulong slot, ulong subcommitteeIndex, Hash256 beaconBlockRoot, CancellationToken cancellationToken) =>
        throw NotServed("GET /eth/v1/validator/sync_committee_contribution");

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<SubmissionFailure>> SubmitContributionAndProofs(IReadOnlyList<SignedContributionAndProof> contributions, CancellationToken cancellationToken) =>
        throw NotServed("POST /eth/v1/validator/contribution_and_proofs");

    private SyncingStatus Status(HeadSnapshot? head)
    {
        ulong headSlot = head?.Status.HeadSlot ?? 0;
        ulong wallSlot = slotClock.CurrentSlot;
        ulong distance = wallSlot > headSlot ? wallSlot - headSlot : 0;
        return new SyncingStatus(headSlot, distance, head is null || distance > ReadySyncDistance, head is not { ExecutionInSync: true }, !engine.IsAvailable);
    }

    /// <summary>The published head and its Fulu post-state, frozen for one call.</summary>
    private (HeadSnapshot Head, BeaconStateFulu State) RequireHeadState()
    {
        HeadSnapshot head = headSnapshots.Current ?? throw NotReady("The node has no head yet.");
        return head.State switch
        {
            ForkedBeaconState.OfFulu fulu => (head, fulu.State),
            ForkedBeaconState.OfGloas => throw NotSupported("The head is a Gloas block; this node serves validator duties up to Fulu."),
            _ => throw StartCapturingHeadState(),
        };
    }

    private BeaconNodeApiException StartCapturingHeadState()
    {
        headSnapshots.CaptureState = true;
        return NotReady("The head state is not captured yet; it is from the next head update on.");
    }

    private void RequireDutyEpoch(ulong epoch)
    {
        if (epoch > slotClock.CurrentEpoch + 1) throw InvalidRequest($"Epoch {epoch} is more than one epoch after the current epoch {slotClock.CurrentEpoch}.");
        RefuseGloas(epoch);
    }

    private void RefuseGloas(ulong epoch)
    {
        if (epoch >= spec.GloasForkEpoch) throw NotSupported($"Epoch {epoch} is a Gloas epoch; this node serves validator duties up to Fulu.");
    }

    /// <summary>The slots the wall clock may be in, allowing <c>MAXIMUM_GOSSIP_CLOCK_DISPARITY</c> either way.</summary>
    private (ulong Earliest, ulong Latest) CurrentSlots()
    {
        ulong currentSlot = slotClock.CurrentSlot;
        long now = slotClock.UnixMilliseconds;
        ulong latest = now + GossipRouter.MaximumGossipClockDisparityMs >= slotClock.SlotStartMilliseconds(currentSlot + 1) ? currentSlot + 1 : currentSlot;
        ulong earliest = currentSlot > 0 && now - GossipRouter.MaximumGossipClockDisparityMs <= slotClock.SlotStartMilliseconds(currentSlot) ? currentSlot - 1 : currentSlot;
        return (earliest, latest);
    }

    private CommitteeCache GetCommittees(BeaconStateFulu state, ulong epoch)
    {
        lock (_committees) return _committees.GetOrBuild(state, epoch);
    }

    /// <summary>The electra/p2p-interface.md <c>beacon_attestation_{subnet_id}</c> checks of a locally submitted attestation.</summary>
    /// <returns>Why the attestation is refused, or <c>null</c> with its subnet and its fork-choice form.</returns>
    private string? Validate(SingleAttestation attestation, BeaconStateFulu state, CheckpointRef finalized, Dictionary<Hash256, ForkChoiceSnapshotNode> nodes,
        ulong earliestSlot, ulong latestSlot, out ulong subnet, out Attestation? vote)
    {
        subnet = 0;
        vote = null;
        if (attestation.Data is not { BeaconBlockRoot: { } blockRoot, Source: not null, Target: { Root: { } targetRoot } target } data) return "The attestation has no data.";
        if (data.Index != 0) return "The attestation data index is not zero.";
        if (data.Slot > latestSlot) return $"Slot {data.Slot} is in the future.";
        ulong epoch = spec.GetEpoch(data.Slot);
        if (epoch + 1 < spec.GetEpoch(earliestSlot)) return $"Epoch {epoch} is neither the current nor the previous epoch.";
        if (target.Epoch != epoch) return $"Target epoch {target.Epoch} is not the epoch {epoch} of the slot.";
        if (!nodes.ContainsKey(blockRoot)) return $"Block {blockRoot} is not known.";
        ulong headEpoch = state.GetCurrentEpoch();
        if (epoch > headEpoch + 1) return $"The head at epoch {headEpoch} is too far behind to know the committees of epoch {epoch}.";

        CommitteeCache committees = GetCommittees(state, epoch);
        if (attestation.CommitteeIndex >= (ulong)committees.CommitteesPerSlot) return $"Committee index {attestation.CommitteeIndex} is out of range ({committees.CommitteesPerSlot} committees per slot).";
        ReadOnlySpan<int> committee = committees.GetBeaconCommittee(data.Slot, (int)attestation.CommitteeIndex);
        int position = attestation.AttesterIndex < int.MaxValue ? committee.IndexOf((int)attestation.AttesterIndex) : -1;
        if (position < 0) return $"Validator {attestation.AttesterIndex} is not in committee {attestation.CommitteeIndex} at slot {data.Slot}.";
        if (CheckpointBlock(nodes, blockRoot, epoch) != targetRoot) return $"Target {targetRoot} is not the checkpoint block of {blockRoot}.";
        if (CheckpointBlock(nodes, blockRoot, finalized.Epoch) != finalized.Root) return $"Block {blockRoot} does not descend from the finalized checkpoint {finalized}.";
        if (!IsSignatureValid(state, attestation.AttesterIndex, data, attestation.Signature)) return "The signature is invalid.";

        // phase0/validator.md compute_subnet_for_attestation.
        subnet = ((ulong)committees.CommitteesPerSlot * (data.Slot % Presets.SlotsPerEpoch) + attestation.CommitteeIndex) % AttestationSubnetCount;
        vote = new Attestation
        {
            AggregationBits = new BitArray(committee.Length) { [position] = true },
            Data = data,
            Signature = attestation.Signature,
            CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [(int)attestation.CommitteeIndex] = true },
        };
        return null;
    }

    private bool IsSignatureValid(BeaconStateFulu state, ulong validatorIndex, AttestationData data, BlsSignature signature)
    {
        if (!SignatureSets.TryGetValidatorKey(pubkeys, state.ReadOnlyValidators, validatorIndex, out G1Affine key)) return false;
        Hash256 domain = state.GetDomain(DomainType.BeaconAttester, data.Target!.Epoch);
        return BlockSignatureBatch.Verify(key, signature, Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), domain), null);
    }

    /// <returns>Why the attestation was not published, or <c>null</c>.</returns>
    private string? Publish(SingleAttestation attestation, ulong subnet)
    {
        string topic = GossipTopics.Topic(ForkDigest.Compute(spec, spec.GetEpoch(attestation.Data!.Slot)), $"beacon_attestation_{subnet}");
        try
        {
            PublishGossip(topic, Snappy.CompressToArray(SingleAttestation.Encode(attestation)));
            return null;
        }
        catch (InvalidOperationException e)
        {
            return $"The attestation was not published: {e.Message}";
        }
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

    /// <summary><c>get_block_root_at_slot(state, compute_start_slot_at_epoch(epoch - 1) - 1)</c> on the head's chain, or the genesis block root when that underflows.</summary>
    private static Hash256 DependentRoot(HeadSnapshot head, BeaconStateFulu state, ulong epoch) =>
        BlockRootAtSlot(head, state, epoch > 1 ? BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch - 1) - 1 : 0);

    /// <summary>The latest block at or before <paramref name="slot"/> on the head's chain, as the head state advanced to that slot names it.</summary>
    private static Hash256 BlockRootAtSlot(HeadSnapshot head, BeaconStateFulu state, ulong slot) =>
        slot >= state.Slot ? head.Status.HeadRoot! : state.GetBlockRootAtSlot(slot);

    private static BeaconNodeApiException NotServed(string route) => NotSupported($"{route} is not served by this node yet.");
    private static BeaconNodeApiException NotSupported(string message) => new(BeaconNodeApiError.NotSupported, message);
    private static BeaconNodeApiException NotReady(string message) => new(BeaconNodeApiError.NotReady, message);
    private static BeaconNodeApiException InvalidRequest(string message) => new(BeaconNodeApiError.InvalidRequest, message);
}
