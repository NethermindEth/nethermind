// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Protocols.Pubsub;
using Snappier;

namespace Nethermind.BeaconChain.ValidatorApi;

/// <summary>Serves <see cref="IBeaconNodeValidatorApi"/> from the head the import worker last published.</summary>
/// <remarks>
/// <para>
/// Every call reads one <see cref="HeadSnapshot"/>, whose post-state and fork-choice copy are never mutated after publication,
/// so no call waits for the import worker. A submitted attestation is checked as gossip against that snapshot on the calling
/// thread by <see cref="SingleAttestationValidator"/>, which also adds it to the <see cref="AttestationPool"/> aggregates are
/// served from, published, and only then queued to the worker for fork choice.
/// </para>
/// <para>
/// A submitted aggregate goes through the <c>beacon_aggregate_and_proof</c> checks a gossip aggregate does: the stateless ones in
/// <see cref="GossipRouter"/> on the calling thread, then the aggregator's and the aggregate's on the worker, which feeds it to fork
/// choice. The call waits for that verdict, at most a third of a slot, and publishes the aggregate once accepted.
/// </para>
/// <para>
/// The worker captures head states only while <see cref="HeadSnapshotHolder.CaptureState"/> is set. The first call that needs
/// one sets it, so a node without a validator client pays nothing, and that call is refused as not ready.
/// </para>
/// <para>Only the attest and aggregate paths are served; a Gloas head or slot is refused as not supported.</para>
/// </remarks>
public sealed class BeaconNodeValidatorApi(
    BeaconChainSpec spec,
    SlotClock slotClock,
    HeadSnapshotHolder headSnapshots,
    IEngineDriver engine,
    BeaconSyncOrchestrator orchestrator,
    BeaconP2P p2p,
    GossipRouter gossip,
    SingleAttestationValidator attestationValidator,
    AttestationPool attestationPool,
    AttestationSubnets attestationSubnets) : IBeaconNodeValidatorApi
{
    /// <summary>Slots the head may lag the wall clock by and still count as synced, as <c>/eth/v1/node/syncing</c> counts them.</summary>
    private const ulong ReadySyncDistance = 1;

    /// <summary>The longest a submitted aggregate waits for the worker's verdict: a third of a slot, so an aggregate due two thirds into its slot is still published within it.</summary>
    private readonly TimeSpan _aggregateVerdictTimeout = TimeSpan.FromMilliseconds(spec.SecondsPerSlot * 1000 * GloasTiming.AttestationDueBps / Presets.BasisPoints);

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

        CommitteeCache committees = attestationValidator.GetCommittees(state, epoch);
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
        (ulong earliestSlot, ulong latestSlot) = attestationValidator.CurrentSlots();
        if (slot < earliestSlot || slot > latestSlot) throw InvalidRequest($"Slot {slot} is not the current slot {slotClock.CurrentSlot}.");
        (HeadSnapshot head, BeaconStateFulu state) = RequireHeadState();
        SyncingStatus status = Status(head);
        if (status.IsSyncing || status.IsOptimistic) throw NotReady(status.IsSyncing ? $"The node is syncing: its head is {status.SyncDistance} slots behind." : "The head's execution payload is not yet verified.");
        if (slot < status.HeadSlot) throw InvalidRequest($"Slot {slot} is before the head at slot {status.HeadSlot}.");

        ulong headEpoch = state.GetCurrentEpoch();
        Checkpoint source = epoch == headEpoch ? state.CurrentJustifiedCheckpoint!
            : epoch == headEpoch + 1 ? EpochProcessing.ComputeJustificationAndFinalization(state, new EpochCache()).CurrentJustifiedCheckpoint
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

        HeadSnapshot head = RequireHeadState().Head;
        if (head.ForkChoice is null) throw NotReady("The node has no fork-choice view yet.");

        List<SubmissionFailure> failures = [];
        for (int i = 0; i < attestations.Count; i++)
        {
            SingleAttestation attestation = attestations[i];
            AttestationCheck check = attestationValidator.Check(attestation, subnet: null);
            string? refusal = check.Refusal ?? Publish(attestation, check.Subnet);
            if (refusal is null)
                orchestrator.QueueGossipAttestation(check.Vote!);
            else
                failures.Add(new SubmissionFailure(i, refusal));
        }

        return new(failures);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// An aggregator's subnet is joined from <see cref="AttestationSubnets.JoinLeadSlots"/> before its slot until the slot has passed
    /// (<see cref="AttestationSubnets"/>); a subscription that does not aggregate needs nothing joined, as its attestation is published by
    /// fanout. A subscription for a slot that has passed is ignored.
    /// </remarks>
    /// <exception cref="BeaconNodeApiException">
    /// <see cref="BeaconNodeApiError.InvalidRequest"/> for a committee outside its slot's committee count or a slot after the next epoch,
    /// and <see cref="BeaconNodeApiError.NotSupported"/> for a Gloas slot; then nothing is subscribed.
    /// </exception>
    public ValueTask SubscribeToBeaconCommittees(IReadOnlyList<BeaconCommitteeSubscription> subscriptions, CancellationToken cancellationToken)
    {
        ulong currentSlot = slotClock.CurrentSlot;
        ulong endSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(slotClock.CurrentEpoch + 2);
        foreach (BeaconCommitteeSubscription subscription in subscriptions)
        {
            if (subscription.CommitteesAtSlot is 0 or > Presets.MaxCommitteesPerSlot || subscription.CommitteeIndex >= subscription.CommitteesAtSlot)
                throw InvalidRequest($"Committee {subscription.CommitteeIndex} is not one of the {subscription.CommitteesAtSlot} committees of slot {subscription.Slot}.");
            if (subscription.Slot >= endSlot) throw InvalidRequest($"Slot {subscription.Slot} is after the epoch following the current epoch {slotClock.CurrentEpoch}.");
            RefuseGloas(spec.GetEpoch(subscription.Slot));
        }

        // Checking the attestations of a joined subnet needs the head state.
        headSnapshots.CaptureState = true;
        foreach (BeaconCommitteeSubscription subscription in subscriptions)
        {
            if (subscription.IsAggregator && subscription.Slot >= currentSlot)
                attestationSubnets.Add(GossipTopics.ComputeSubnetForAttestation(subscription.CommitteesAtSlot, subscription.Slot, subscription.CommitteeIndex), subscription.Slot);
        }

        attestationSubnets.Update(currentSlot);
        return default;
    }

    /// <inheritdoc/>
    /// <remarks>Served from the <see cref="AttestationPool"/>, which aggregates every attestation the node validated for the vote.</remarks>
    public ValueTask<Attestation> GetAggregateAttestation(ulong slot, Hash256 attestationDataRoot, ulong committeeIndex, CancellationToken cancellationToken)
    {
        RefuseGloas(spec.GetEpoch(slot));
        return new(attestationPool.GetAggregate(slot, attestationDataRoot, committeeIndex)
            ?? throw new BeaconNodeApiException(BeaconNodeApiError.NotFound, $"No attestation of committee {committeeIndex} at slot {slot} with data root {attestationDataRoot} is held."));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// An aggregate ignored because gossip already covers it, by its aggregator or by a superset of its bits, is neither published
    /// again nor refused: the network has it (p2p-interface.md <c>beacon_aggregate_and_proof</c> Seen).
    /// </remarks>
    /// <exception cref="BeaconNodeApiException"><see cref="BeaconNodeApiError.NotReady"/> before gossip has started.</exception>
    public async ValueTask<IReadOnlyList<SubmissionFailure>> SubmitAggregateAndProofs(IReadOnlyList<SignedAggregateAndProof> aggregates, CancellationToken cancellationToken)
    {
        foreach (SignedAggregateAndProof aggregate in aggregates)
        {
            if (aggregate.Message?.Aggregate?.Data is { } data) RefuseGloas(spec.GetEpoch(data.Slot));
        }

        if (!orchestrator.GossipStarted) throw NotReady("Gossip has not started yet; the node is still catching up.");

        Task<string?>[] submissions = new Task<string?>[aggregates.Count];
        for (int i = 0; i < submissions.Length; i++) submissions[i] = SubmitAggregate(aggregates[i], cancellationToken);
        string?[] refusals = await Task.WhenAll(submissions);

        List<SubmissionFailure> failures = [];
        for (int i = 0; i < refusals.Length; i++)
        {
            if (refusals[i] is { } refusal) failures.Add(new SubmissionFailure(i, refusal));
        }

        return failures;
    }

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

    /// <returns>Why the aggregate was not published, or <c>null</c>.</returns>
    private async Task<string?> SubmitAggregate(SignedAggregateAndProof aggregate, CancellationToken cancellationToken)
    {
        if (aggregate.Message is not { Aggregate: { Data.Target: not null, CommitteeBits: { } committeeBits, AggregationBits: { } aggregationBits } attestation } message)
            return "The aggregate has no attestation.";

        byte[] compressed = Snappy.CompressToArray(SignedAggregateAndProof.Encode(aggregate));
        TaskCompletionSource<MessageValidity> verdictGiven = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GossipVerdict verdict = new(verdictGiven.TrySetResult, null);
        MessageValidity checkedHere = gossip.Handle(GossipTopics.BeaconAggregateAndProof, gloasTopic: false, compressed, verdict);
        if (!verdict.IsHandedOff) verdict.Complete(checkedHere);

        MessageValidity validity;
        try
        {
            validity = await verdictGiven.Task.WaitAsync(_aggregateVerdictTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return "The node did not finish checking the aggregate in time.";
        }

        return validity switch
        {
            MessageValidity.Ignored when gossip.IsAggregateSeen(attestation.Data, committeeBits, aggregationBits, message.AggregatorIndex) => null,
            MessageValidity.Accepted => Publish(GossipTopics.Topic(ForkDigest.Compute(spec, spec.GetEpoch(attestation.Data.Slot)), GossipTopics.BeaconAggregateAndProof), compressed, "aggregate"),
            MessageValidity.Rejected => "The aggregate fails a beacon_aggregate_and_proof gossip rule.",
            MessageValidity.Throttled => "The node is too busy to check the aggregate.",
            _ => "The aggregate cannot be checked now, such as for a block the node does not know or a slot outside the current and previous epoch.",
        };
    }

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

    /// <returns>Why the attestation was not published, or <c>null</c>.</returns>
    private string? Publish(SingleAttestation attestation, ulong subnet) =>
        Publish(GossipTopics.Topic(ForkDigest.Compute(spec, spec.GetEpoch(attestation.Data!.Slot)), GossipTopics.BeaconAttestationTopicNames[subnet]),
            Snappy.CompressToArray(SingleAttestation.Encode(attestation)), "attestation");

    /// <returns>Why the message was not published, or <c>null</c>.</returns>
    private string? Publish(string topic, byte[] message, string what)
    {
        try
        {
            PublishGossip(topic, message);
            return null;
        }
        catch (InvalidOperationException e)
        {
            return $"The {what} was not published: {e.Message}";
        }
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
