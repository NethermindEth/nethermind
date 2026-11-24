// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CkzgLib;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.DataAvailability;
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
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Merge.Plugin.Data;
using Snappier;

namespace Nethermind.BeaconChain.ValidatorApi;

/// <summary>Serves <see cref="IBeaconNodeValidatorApi"/> from the head the import worker last published.</summary>
/// <remarks>
/// <para>
/// Every call reads one <see cref="HeadSnapshot"/>, whose post-state and fork-choice copy are never mutated after publication,
/// so reading duties never waits for the import worker. A submitted attestation is checked as gossip against that snapshot on the calling
/// thread by <see cref="SingleAttestationValidator"/>, which also adds it to the <see cref="AttestationPool"/> aggregates are
/// served from, published, and only then queued to the worker for fork choice.
/// </para>
/// <para>
/// A submitted aggregate goes through the <c>beacon_aggregate_and_proof</c> checks a gossip aggregate does: the stateless ones in
/// <see cref="GossipRouter"/> on the calling thread, then the aggregator's and the aggregate's on the worker, which feeds it to fork
/// choice. The call waits for that verdict, at most a third of a slot, and publishes the aggregate once accepted.
/// </para>
/// <para>
/// Sync committee messages and contributions go through their gossip checks in <see cref="SyncCommitteeValidator"/> on the calling thread
/// against the same snapshot, are published, and are pooled in the <see cref="SyncCommitteePool"/> contributions are served from; they never
/// reach the worker, as fork choice does not count them.
/// </para>
/// <para>
/// The worker captures head states only while <see cref="HeadSnapshotHolder.CaptureState"/> is set. The first call that needs
/// one sets it, so a node without a validator client pays nothing, and that call is refused as not ready.
/// </para>
/// <para>
/// A block is built on the import worker by <see cref="BlockProducer"/>, as only the worker holds the head's state and drives the execution
/// layer. A published block is checked as gossip on the calling thread, published with its data column sidecars, and queued to the worker
/// for import.
/// </para>
/// <para>Validator duties are served up to Fulu; a Gloas head or slot is refused as not supported.</para>
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
    AttestationSubnets attestationSubnets,
    SyncCommitteeValidator syncCommitteeValidator,
    SyncCommitteePool syncCommitteePool,
    SyncCommitteeSubnets syncCommitteeSubnets,
    ProposerPreparations proposerPreparations,
    BlockAttestationPool blockAttestationPool,
    PubkeyCache pubkeys) : IBeaconNodeValidatorApi
{
    /// <summary>Slots the head may lag the wall clock by and still count as synced, as <c>/eth/v1/node/syncing</c> counts them.</summary>
    private const ulong ReadySyncDistance = 1;

    /// <summary>The longest a submitted aggregate waits for the worker's verdict: a third of a slot, so an aggregate due two thirds into its slot is still published within it.</summary>
    private readonly TimeSpan _aggregateVerdictTimeout = TimeSpan.FromMilliseconds(spec.SecondsPerSlot * 1000 * GloasTiming.AttestationDueBps / Presets.BasisPoints);

    /// <summary>The longest a block is waited for: a third of a slot, past which attesters have voted without it.</summary>
    private readonly TimeSpan _productionTimeout = TimeSpan.FromMilliseconds(spec.SecondsPerSlot * 1000 * GloasTiming.AttestationDueBps / Presets.BasisPoints);

    private static readonly ulong[] AllColumns = CreateAllColumns();

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
    /// <remarks>A preparation holds through the current epoch and the <see cref="ProposerPreparations.RetainedEpochs"/> after it.</remarks>
    /// <exception cref="BeaconNodeApiException">
    /// <see cref="BeaconNodeApiError.InvalidRequest"/> when the node would hold more than <see cref="ProposerPreparations.MaxPreparations"/> validators;
    /// then nothing is prepared.
    /// </exception>
    public ValueTask PrepareBeaconProposers(IReadOnlyList<ProposerPreparation> preparations, CancellationToken cancellationToken) =>
        proposerPreparations.TryPrepare(preparations) ? default : throw InvalidRequest($"The node holds the fee recipients of at most {ProposerPreparations.MaxPreparations} validators.");

    /// <inheritdoc/>
    /// <remarks>
    /// The aggregates the <see cref="AttestationPool"/> holds join the <see cref="BlockAttestationPool"/> here, off the import worker, which then
    /// builds the block with <see cref="BlockProducer"/>; the call waits for it at most a third of a slot.
    /// </remarks>
    /// <exception cref="BeaconNodeApiException">
    /// <see cref="BeaconNodeApiError.InvalidRequest"/> for a slot other than the current one or not after the head, a randao reveal that is not the
    /// proposer's or a proposer with no fee recipient prepared; <see cref="BeaconNodeApiError.NotReady"/> also when the worker is too busy, the
    /// execution layer builds no usable payload or the block fails its own state transition.
    /// </exception>
    public async ValueTask<ProducedBlock> ProduceBlock(ulong slot, BlsSignature randaoReveal, Hash256 graffiti, CancellationToken cancellationToken)
    {
        RefuseGloas(spec.GetEpoch(slot));
        (_, ulong latestSlot) = attestationValidator.CurrentSlots();
        if (slot < slotClock.CurrentSlot || slot > latestSlot) throw InvalidRequest($"Slot {slot} is not the current slot {slotClock.CurrentSlot}.");
        HeadSnapshot head = RequireHeadState().Head;
        SyncingStatus status = Status(head);
        if (status.IsSyncing || status.IsOptimistic) throw NotReady(status.IsSyncing ? $"The node is syncing: its head is {status.SyncDistance} slots behind." : "The head's execution payload is not yet verified.");
        if (slot <= status.HeadSlot) throw InvalidRequest($"Slot {slot} is not after the head at slot {status.HeadSlot}.");

        foreach (Attestation aggregate in attestationPool.GetAggregates()) blockAttestationPool.Add(aggregate);
        TaskCompletionSource<ProducedBlock> produced = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!orchestrator.WorkWriter.TryWrite(new BeaconSyncOrchestrator.ProduceBlockItem(slot, randaoReveal, graffiti, produced)))
            throw NotReady("The import worker is too busy to build a block.");
        try
        {
            return await produced.Task.WaitAsync(_productionTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            throw NotReady($"The node did not build the block of slot {slot} in time.");
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// beacon-APIs <c>broadcast_validation</c> <c>gossip</c>: before anything is published, the block passes the fulu/p2p-interface.md
    /// <c>beacon_block</c> checks the node can make against the head snapshot (<see cref="CheckGossipBlock"/>), and its blobs and cell proofs must
    /// match its commitments, from which every data column sidecar is computed (fulu/validator.md <c>get_data_column_sidecars_from_block</c>).
    /// </para>
    /// <para>
    /// The block and its sidecars are then published, and the block is queued to the import worker as one from gossip, with the sidecars of the
    /// columns the node samples. The call returns once it is queued, so a block that fails its full import was still published, which the route
    /// answers with 202 and this call does not report.
    /// </para>
    /// </remarks>
    /// <exception cref="BeaconNodeApiException">
    /// <see cref="BeaconNodeApiError.InvalidRequest"/> as the interface says; <see cref="BeaconNodeApiError.NotReady"/> before gossip has started,
    /// or when publishing failed.
    /// </exception>
    public async ValueTask PublishBlock(SignedBlockContents contents, CancellationToken cancellationToken)
    {
        SignedBeaconBlock signed = contents.SignedBlock;
        if (signed.Message is not { Body.ExecutionPayload: not null } block) throw InvalidRequest("The block has no body or no execution payload.");
        RefuseGloas(spec.GetEpoch(block.Slot));
        (HeadSnapshot head, BeaconStateFulu state) = RequireHeadState();
        if (!orchestrator.GossipStarted) throw NotReady("Gossip has not started yet; the node is still catching up.");
        if (CheckGossipBlock(head, state, signed) is { } refusal) throw InvalidRequest(refusal);
        DataColumnSidecar[] columns = DataColumns(signed, contents) ?? throw InvalidRequest("The blobs or their cell proofs do not match the block's KZG commitments.");

        byte[] digest = ForkDigest.Compute(spec, spec.GetEpoch(block.Slot));
        string? failure = Publish(GossipTopics.Topic(digest, GossipTopics.BeaconBlock), Snappy.CompressToArray(SignedBeaconBlock.Encode(signed)), "block");
        if (failure is not null) throw NotReady(failure);
        gossip.MarkProposalSeen(block.Slot, block.ProposerIndex);
        foreach (DataColumnSidecar column in columns)
        {
            string topic = GossipTopics.Topic(digest, GossipTopics.DataColumnSidecarTopicName(CustodyGroups.ComputeSubnetForDataColumnSidecar(column.Index)));
            string? columnFailure = Publish(topic, Snappy.CompressToArray(DataColumnSidecar.Encode(column)), "data column sidecar");
            failure ??= columnFailure;
        }

        await orchestrator.WorkWriter.WriteAsync(new BeaconSyncOrchestrator.LocalBlockItem(signed, SszRoots.HashTreeRoot(block), columns), cancellationToken);
        if (failure is not null) throw NotReady($"The block was published, but not all of its data column sidecars: {failure}");
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The subnet of each position is joined at once, until the subscription's <c>until_epoch</c> (<see cref="SyncCommitteeSubnets"/>): the
    /// validator client chooses how far ahead of its period it subscribes.
    /// </remarks>
    /// <exception cref="BeaconNodeApiException">
    /// <see cref="BeaconNodeApiError.InvalidRequest"/> for a position outside the committee or an <c>until_epoch</c> after the next sync
    /// committee period; then nothing is subscribed.
    /// </exception>
    public ValueTask SubscribeToSyncCommittees(IReadOnlyList<SyncCommitteeSubscription> subscriptions, CancellationToken cancellationToken)
    {
        ulong currentEpoch = slotClock.CurrentEpoch;
        ulong endEpoch = (currentEpoch / Presets.EpochsPerSyncCommitteePeriod + 2) * Presets.EpochsPerSyncCommitteePeriod;
        foreach (SyncCommitteeSubscription subscription in subscriptions)
        {
            if (subscription.UntilEpoch > endEpoch) throw InvalidRequest($"Until epoch {subscription.UntilEpoch} is after the next sync committee period, which ends at epoch {endEpoch}.");
            foreach (ulong position in subscription.SyncCommitteeIndices)
            {
                if (position >= Presets.SyncCommitteeSize) throw InvalidRequest($"Sync committee index {position} is not below the committee size {Presets.SyncCommitteeSize}.");
            }
        }

        // Checking the messages of a joined subnet needs the head state.
        headSnapshots.CaptureState = true;
        foreach (SyncCommitteeSubscription subscription in subscriptions)
        {
            foreach (ulong position in subscription.SyncCommitteeIndices)
                syncCommitteeSubnets.Add(position / SyncCommitteeContribution.SubcommitteeSize, subscription.UntilEpoch);
        }

        syncCommitteeSubnets.Update(currentEpoch);
        return default;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Applies the altair/p2p-interface.md <c>sync_committee_{subnet_id}</c> checks against the head snapshot for every subnet of the
    /// validator, except the per-subnet "already seen" IGNORE, which guards relays rather than a node's own validators.
    /// </remarks>
    public ValueTask<IReadOnlyList<SubmissionFailure>> SubmitSyncCommitteeMessages(IReadOnlyList<SyncCommitteeMessage> messages, CancellationToken cancellationToken)
    {
        foreach (SyncCommitteeMessage message in messages) RefuseGloas(spec.GetEpoch(message.Slot));
        RequireHeadState();

        List<SubmissionFailure> failures = [];
        for (int i = 0; i < messages.Count; i++)
        {
            SyncCommitteeCheck check = syncCommitteeValidator.CheckMessage(messages[i], subnet: null);
            if ((check.Refusal ?? Publish(messages[i], check.Subnets)) is { } refusal) failures.Add(new SubmissionFailure(i, refusal));
        }

        return new(failures);
    }

    /// <inheritdoc/>
    /// <remarks>Served from the <see cref="SyncCommitteePool"/>, which aggregates every message the node validated for the vote.</remarks>
    /// <exception cref="BeaconNodeApiException">
    /// <see cref="BeaconNodeApiError.NotReady"/> while the block is optimistic (beacon-APIs <c>produceSyncCommitteeContribution</c>), and
    /// <see cref="BeaconNodeApiError.InvalidRequest"/> for a subcommittee index past the last.
    /// </exception>
    public ValueTask<SyncCommitteeContribution> ProduceSyncCommitteeContribution(ulong slot, ulong subcommitteeIndex, Hash256 beaconBlockRoot, CancellationToken cancellationToken)
    {
        RefuseGloas(spec.GetEpoch(slot));
        if (subcommitteeIndex >= SyncCommitteeContribution.SubnetCount) throw InvalidRequest($"Subcommittee index {subcommitteeIndex} is not below {SyncCommitteeContribution.SubnetCount}.");
        foreach (ForkChoiceSnapshotNode node in headSnapshots.Current?.ForkChoice?.Nodes ?? [])
        {
            if (node.Root == beaconBlockRoot && node.ExecutionStatus == ExecutionStatus.Optimistic) throw NotReady($"Block {beaconBlockRoot} is optimistic.");
        }

        return new(syncCommitteePool.GetContribution(slot, beaconBlockRoot, subcommitteeIndex)
            ?? throw new BeaconNodeApiException(BeaconNodeApiError.NotFound, $"No sync committee message of subcommittee {subcommitteeIndex} at slot {slot} for block {beaconBlockRoot} is held."));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Applies the altair/p2p-interface.md <c>sync_committee_contribution_and_proof</c> checks against the head snapshot. A contribution
    /// ignored because gossip already covers it, by its aggregator or by a superset of its bits, is neither published again nor refused:
    /// the network has it.
    /// </remarks>
    public ValueTask<IReadOnlyList<SubmissionFailure>> SubmitContributionAndProofs(IReadOnlyList<SignedContributionAndProof> contributions, CancellationToken cancellationToken)
    {
        foreach (SignedContributionAndProof contribution in contributions)
        {
            if (contribution.Message?.Contribution is { } contributed) RefuseGloas(spec.GetEpoch(contributed.Slot));
        }

        RequireHeadState();
        List<SubmissionFailure> failures = [];
        for (int i = 0; i < contributions.Count; i++)
        {
            SignedContributionAndProof contribution = contributions[i];
            SyncCommitteeCheck check = syncCommitteeValidator.CheckContribution(contribution);
            string? refusal = check switch
            {
                { Validity: MessageValidity.Accepted } => Publish(GossipTopics.Topic(ForkDigest.Compute(spec, spec.GetEpoch(contribution.Message!.Contribution!.Slot)), GossipTopics.SyncCommitteeContributionAndProof),
                    Snappy.CompressToArray(SignedContributionAndProof.Encode(contribution)), "contribution"),
                { Validity: MessageValidity.Ignored, Reason: GossipDropReason.Duplicate } => null,
                _ => check.Refusal,
            };
            if (refusal is not null) failures.Add(new SubmissionFailure(i, refusal));
        }

        return new(failures);
    }

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

    /// <summary>The fulu/p2p-interface.md <c>beacon_block</c> checks of <paramref name="signed"/> the node can make against the head snapshot.</summary>
    /// <remarks>The expected proposer is checked only for a block on the head, whose state holds the lookahead of the block's slot.</remarks>
    /// <returns>Why the block must not be published, or <c>null</c>.</returns>
    private string? CheckGossipBlock(HeadSnapshot head, BeaconStateFulu state, SignedBeaconBlock signed)
    {
        BeaconBlock block = signed.Message!;
        (_, ulong latestSlot) = attestationValidator.CurrentSlots();
        if (block.Slot > latestSlot) return $"Slot {block.Slot} is in the future.";
        ulong finalizedSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch(head.Status.FinalizedEpoch);
        if (block.Slot <= finalizedSlot) return $"Slot {block.Slot} is not after the finalized slot {finalizedSlot}.";
        if (gossip.IsProposalSeen(block.Slot, block.ProposerIndex)) return $"A block of proposer {block.ProposerIndex} at slot {block.Slot} was already seen.";

        ForkChoiceSnapshotNode? parent = null;
        foreach (ForkChoiceSnapshotNode node in head.ForkChoice?.Nodes ?? [])
        {
            if (node.Root == block.ParentRoot) parent = node;
        }

        if (parent is null) return $"The parent {block.ParentRoot} is not known.";
        if (block.Slot <= parent.Slot) return $"Slot {block.Slot} is not after the parent's slot {parent.Slot}.";
        ulong headEpoch = state.GetCurrentEpoch();
        ulong blockEpoch = spec.GetEpoch(block.Slot);
        if (block.ParentRoot == head.Status.HeadRoot && blockEpoch <= headEpoch + 1)
        {
            ulong expected = state.ProposerLookahead![(int)((blockEpoch - headEpoch) * Presets.SlotsPerEpoch + block.Slot % Presets.SlotsPerEpoch)];
            if (expected != block.ProposerIndex) return $"Proposer {block.ProposerIndex} is not the proposer {expected} of slot {block.Slot}.";
        }

        try
        {
            if (!SignatureSets.VerifyProposerSignature(state, signed, pubkeys)) return "The proposer signature is invalid.";
        }
        catch (BeaconStateException e)
        {
            return e.Message;
        }

        BeaconBlockBody body = block.Body!;
        ulong timestamp = state.GenesisTime + block.Slot * Presets.SecondsPerSlot;
        if (body.ExecutionPayload!.Timestamp != timestamp) return $"The payload timestamp {body.ExecutionPayload.Timestamp} is not the time {timestamp} of slot {block.Slot}.";
        ulong maxBlobs = spec.GetBlobParameters(blockEpoch)?.MaxBlobsPerBlock ?? spec.MaxBlobsPerBlockElectra;
        int blobCount = body.BlobKzgCommitments?.Length ?? 0;
        return (ulong)blobCount > maxBlobs ? $"The block has {blobCount} blob commitments, more than the {maxBlobs} allowed." : null;
    }

    /// <summary>Every data column sidecar of the block, computed from its blobs and checked against their cell proofs.</summary>
    /// <returns>The sidecars in column order, none for a block without blobs, or <c>null</c> when the blobs or proofs do not match the commitments.</returns>
    private static DataColumnSidecar[]? DataColumns(SignedBeaconBlock signed, SignedBlockContents contents)
    {
        int blobCount = signed.Message!.Body!.BlobKzgCommitments?.Length ?? 0;
        if (contents.Blobs.Count != blobCount || contents.KzgProofs.Count != blobCount * Ckzg.CellsPerExtBlob) return null;
        if (blobCount == 0) return [];

        BlobAndProofV2[] blobs = new BlobAndProofV2[blobCount];
        for (int i = 0; i < blobCount; i++)
        {
            byte[][] proofs = new byte[Ckzg.CellsPerExtBlob][];
            for (int cell = 0; cell < proofs.Length; cell++) proofs[cell] = contents.KzgProofs[i * Ckzg.CellsPerExtBlob + cell];
            blobs[i] = new BlobAndProofV2(contents.Blobs[i], proofs);
        }

        DataColumnSidecar[] columns = LocalBlobColumns.Create(signed, blobs, AllColumns);
        return columns.Length == AllColumns.Length ? columns : null;
    }

    private static ulong[] CreateAllColumns()
    {
        ulong[] columns = new ulong[Eip7594DasConstants.NumberOfColumns];
        for (int column = 0; column < columns.Length; column++) columns[column] = (ulong)column;
        return columns;
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

    /// <summary>Publishes <paramref name="message"/> on each sync committee subnet set in <paramref name="subnets"/>.</summary>
    /// <returns>Why the message was not published, or <c>null</c>.</returns>
    private string? Publish(SyncCommitteeMessage message, ulong subnets)
    {
        byte[] digest = ForkDigest.Compute(spec, spec.GetEpoch(message.Slot));
        byte[] compressed = Snappy.CompressToArray(SyncCommitteeMessage.Encode(message));
        for (int subnet = 0; subnet < SyncCommitteeContribution.SubnetCount; subnet++)
        {
            if ((subnets >> subnet & 1) != 0 && Publish(GossipTopics.Topic(digest, GossipTopics.SyncCommitteeTopicNames[subnet]), compressed, "sync committee message") is { } refusal)
                return refusal;
        }

        return null;
    }

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

    private static BeaconNodeApiException NotSupported(string message) => new(BeaconNodeApiError.NotSupported, message);
    private static BeaconNodeApiException NotReady(string message) => new(BeaconNodeApiError.NotReady, message);
    private static BeaconNodeApiException InvalidRequest(string message) => new(BeaconNodeApiError.InvalidRequest, message);
}
