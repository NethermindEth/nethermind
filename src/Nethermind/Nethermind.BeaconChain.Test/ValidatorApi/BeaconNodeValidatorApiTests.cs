// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.BeaconChain.ValidatorApi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Snappier;

namespace Nethermind.BeaconChain.Test.ValidatorApi;

public class BeaconNodeValidatorApiTests
{
    private const int ValidatorCount = 64;
    private const long AttestationTimeMs = 4000;
    private static readonly ulong HeadEpoch = BeaconChainSpec.Mainnet.FuluForkEpoch + 10;
    private static readonly ulong EpochStart = BeaconStateAccessors.ComputeStartSlotAtEpoch(HeadEpoch);
    private static readonly ulong HeadSlot = EpochStart + 2;
    private static readonly ulong SyncPeriod = HeadEpoch / Presets.EpochsPerSyncCommitteePeriod;
    private static readonly CheckpointRef Finalized = new(HeadEpoch - 2, Keccak.Compute("finalized"));
    private static readonly Hash256 ParentRoot = Keccak.Compute("last block of the previous epoch");
    private static readonly Hash256 BoundaryRoot = Keccak.Compute("first block of the head epoch");
    private static readonly Hash256 HeadRoot = Keccak.Compute("head");
    private static readonly Hash256 SiblingRoot = Keccak.Compute("sibling of the boundary block");
    private static readonly Hash256 OrphanRoot = Keccak.Compute("block off the finalized chain");

    private static readonly ForkChoiceSnapshotNode[] Nodes =
    [
        Node(BeaconStateAccessors.ComputeStartSlotAtEpoch(Finalized.Epoch), Finalized.Root, null),
        Node(EpochStart - 1, ParentRoot, Finalized.Root),
        Node(EpochStart, BoundaryRoot, ParentRoot),
        Node(EpochStart, SiblingRoot, ParentRoot),
        Node(HeadSlot, HeadRoot, BoundaryRoot),
        Node(EpochStart - 5, OrphanRoot, null),
    ];

    public enum HeadKind { Synced, Optimistic, Gloas }

    public enum Flaw { None, NonZeroIndex, FutureSlot, TargetEpoch, UnknownBlock, CommitteeIndex, NotInCommittee, Signature, TargetNotAncestor, NotFinalizedDescendant }

    [TestCase(0UL, true, false, false)]
    [TestCase(2UL, true, true, false)]
    [TestCase(1UL, false, false, true)]
    public async Task Syncing_status_and_head_root_follow_the_published_head(ulong headLag, bool executionInSync, bool syncing, bool optimistic)
    {
        using ValidatorNode node = new(Presets.FarFutureEpoch, HeadSlot + headLag, AttestationTimeMs);
        node.Heads.Current = Head(HeadState(HeadSlot), HeadRoot, executionInSync);

        SyncingStatus status = await node.Api.GetSyncingStatus(CancellationToken.None);
        Hash256 headRoot = await node.Api.GetHeadBlockRoot(CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(status, Is.EqualTo(new SyncingStatus(HeadSlot, headLag, syncing, optimistic, ExecutionLayerOffline: false)));
        Assert.That(headRoot, Is.EqualTo(HeadRoot));
    }

    [Test]
    public async Task Calls_are_not_ready_before_a_head_and_its_state_and_the_first_one_needing_the_state_turns_its_capture_on()
    {
        using ValidatorNode node = new(Presets.FarFutureEpoch, HeadSlot, AttestationTimeMs);
        SyncingStatus beforeHead = await node.Api.GetSyncingStatus(CancellationToken.None);
        BeaconNodeApiError? headRootBeforeHead = await Refusal(() => node.Api.GetHeadBlockRoot(CancellationToken.None).AsTask());
        node.Heads.Current = Head(HeadState(HeadSlot), HeadRoot, executionInSync: true) with { State = null };
        bool capturedBefore = node.Heads.CaptureState;

        BeaconNodeApiError? dutiesWithoutState = await Refusal(() => node.Api.GetProposerDuties(HeadEpoch, CancellationToken.None).AsTask());

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(beforeHead.IsSyncing, Is.True);
        Assert.That(headRootBeforeHead, Is.EqualTo(BeaconNodeApiError.NotReady));
        Assert.That(capturedBefore, Is.False, "fixture: the REST API is off, so nothing captured head states");
        Assert.That(dutiesWithoutState, Is.EqualTo(BeaconNodeApiError.NotReady));
        Assert.That(node.Heads.CaptureState, Is.True);
    }

    [Test]
    public async Task Validators_are_found_by_public_key_in_registry_order()
    {
        using ValidatorNode node = new(Presets.FarFutureEpoch, HeadSlot, AttestationTimeMs);
        BeaconStateFulu state = HeadState(HeadSlot);
        Validator[] validators = state.Validators!;
        validators[5].Slashed = true;
        validators[5].ExitEpoch = HeadEpoch + 5;
        node.Heads.Current = Head(state, HeadRoot, executionInSync: true);

        IReadOnlyList<ValidatorSummary> found = await node.Api.GetValidators(
            [validators[5].Pubkey, GloasTestFixtures.Pubkey(0xaa), validators[2].Pubkey, validators[5].Pubkey], CancellationToken.None);

        Assert.That(found, Is.EqualTo(new[]
        {
            new ValidatorSummary(validators[2].Pubkey, 2, 0, Presets.FarFutureEpoch, false),
            new ValidatorSummary(validators[5].Pubkey, 5, 0, HeadEpoch + 5, true),
        }));
    }

    [Test]
    public async Task Attester_duties_place_each_requested_validator_in_its_committee([Values(0UL, 1UL)] ulong epochsAhead)
    {
        using ValidatorNode node = new(Presets.FarFutureEpoch, HeadSlot, AttestationTimeMs);
        BeaconStateFulu state = HeadState(HeadSlot);
        node.Heads.Current = Head(state, HeadRoot, executionInSync: false);
        ulong epoch = HeadEpoch + epochsAhead;

        DutiesResult<AttesterDuty> result = await node.Api.GetAttesterDuties(epoch, [0, 7, 63, 1000], CancellationToken.None);

        CommitteeCache committees = CommitteeCache.Build(state, epoch);
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(result.Duties.Select(static duty => duty.ValidatorIndex), Is.EquivalentTo(new ulong[] { 0, 7, 63 }), "an active validator attests once an epoch; an unknown one never");
        foreach (AttesterDuty duty in result.Duties)
        {
            int[] committee = committees.GetBeaconCommittee(duty.Slot, (int)duty.CommitteeIndex).ToArray();
            Assert.That(committee[(int)duty.ValidatorCommitteeIndex], Is.EqualTo((int)duty.ValidatorIndex));
            Assert.That((duty.CommitteeLength, duty.CommitteesAtSlot, BeaconChainSpec.Mainnet.GetEpoch(duty.Slot)), Is.EqualTo(((ulong)committee.Length, (ulong)committees.CommitteesPerSlot, epoch)));
            Assert.That(duty.PublicKey, Is.EqualTo(state.Validators![(int)duty.ValidatorIndex].Pubkey));
        }
        Assert.That(result.DependentRoot, Is.EqualTo(DependentRoot(epoch)));
        Assert.That(result.ExecutionOptimistic, Is.True);
    }

    [Test]
    public async Task Proposer_duties_read_the_lookahead_of_the_requested_epoch([Values(0UL, 1UL)] ulong epochsAhead)
    {
        using ValidatorNode node = new(Presets.FarFutureEpoch, HeadSlot, AttestationTimeMs);
        BeaconStateFulu state = HeadState(HeadSlot);
        node.Heads.Current = Head(state, HeadRoot, executionInSync: true);
        ulong epoch = HeadEpoch + epochsAhead;

        DutiesResult<ProposerDuty> result = await node.Api.GetProposerDuties(epoch, CancellationToken.None);

        ProposerDuty[] expected = [.. Enumerable.Range(0, (int)Presets.SlotsPerEpoch).Select(slotInEpoch =>
        {
            ulong proposer = state.ProposerLookahead![(int)(epochsAhead * Presets.SlotsPerEpoch) + slotInEpoch];
            return new ProposerDuty(state.Validators![(int)proposer].Pubkey, proposer, BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch) + (ulong)slotInEpoch);
        })];
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(result.Duties, Is.EqualTo(expected));
        Assert.That(result.DependentRoot, Is.EqualTo(DependentRoot(epoch)));
        Assert.That(result.ExecutionOptimistic, Is.False);
    }

    [Test]
    public async Task Sync_duties_list_every_position_a_validator_holds_in_the_committee_of_the_period([Values] bool nextPeriod)
    {
        using ValidatorNode node = new(Presets.FarFutureEpoch, HeadSlot, AttestationTimeMs);
        BeaconStateFulu state = HeadState(HeadSlot);
        node.Heads.Current = Head(state, HeadRoot, executionInSync: true);
        ulong period = SyncPeriod + (nextPeriod ? 1UL : 0UL);

        DutiesResult<SyncDuty> result = await node.Api.GetSyncDuties(period * Presets.EpochsPerSyncCommitteePeriod, [0, 3, 63], CancellationToken.None);

        // HeadState fills the current committee with validators 0, 1, 2 in turn and the next with 3, 4.
        ulong member = nextPeriod ? 3UL : 0UL;
        IEnumerable<ulong> positions = Enumerable.Range(0, Presets.SyncCommitteeSize).Where(position => position % (nextPeriod ? 2 : 3) == 0).Select(static position => (ulong)position);
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(result.Duties.Select(static duty => duty.ValidatorIndex), Is.EqualTo(new[] { member }));
        Assert.That(result.Duties[0].PublicKey, Is.EqualTo(state.Validators![(int)member].Pubkey));
        Assert.That(result.Duties[0].ValidatorSyncCommitteeIndices, Is.EqualTo(positions));
        Assert.That(result.DependentRoot, Is.Null);
    }

    [Test]
    public async Task Attestation_data_votes_for_the_head_with_the_source_of_its_state_processed_to_the_slot([Values] bool crossesEpoch)
    {
        ulong headSlot = crossesEpoch ? EpochStart - 1 : HeadSlot;
        ulong slot = headSlot + 1;
        Hash256 headRoot = crossesEpoch ? ParentRoot : HeadRoot;
        BeaconStateFulu state = HeadState(headSlot);
        // Every validator voted for the head epoch's target, so epoch processing at its end justifies it.
        state.CurrentEpochParticipation = [.. Enumerable.Repeat((byte)(1 << Presets.TimelyTargetFlagIndex), ValidatorCount)];
        BeaconStateFulu processed = state.Clone();
        SlotProcessing.ProcessSlots(processed, slot, new EpochCache());
        using ValidatorNode node = new(Presets.FarFutureEpoch, slot, AttestationTimeMs);
        node.Heads.Current = Head(state, headRoot, executionInSync: true);

        AttestationData data = await node.Api.ProduceAttestationData(slot, committeeIndex: 5, CancellationToken.None);

        CheckpointRef source = CheckpointRef.From(processed.CurrentJustifiedCheckpoint!);
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(source == CheckpointRef.From(state.CurrentJustifiedCheckpoint!), Is.EqualTo(!crossesEpoch), "fixture: only the epoch boundary moves the source");
        Assert.That((data.Slot, data.Index, data.BeaconBlockRoot), Is.EqualTo((slot, 0UL, headRoot)));
        Assert.That(CheckpointRef.From(data.Source!), Is.EqualTo(source), "phase0/validator.md: the source of the head state processed to the slot");
        Assert.That(CheckpointRef.From(data.Target!), Is.EqualTo(new CheckpointRef(HeadEpoch, crossesEpoch ? ParentRoot : BoundaryRoot)));
    }

    private static IEnumerable<TestCaseData> Refusals()
    {
        ulong nextEpochStart = BeaconStateAccessors.ComputeStartSlotAtEpoch(HeadEpoch + 1);
        yield return Refused("Attester duties more than an epoch after the wall clock", HeadSlot, AttestationTimeMs, Presets.FarFutureEpoch, HeadKind.Synced,
            api => api.GetAttesterDuties(HeadEpoch + 2, [0], CancellationToken.None).AsTask(), BeaconNodeApiError.InvalidRequest);
        yield return Refused("Attester duties before the previous epoch of the head", HeadSlot, AttestationTimeMs, Presets.FarFutureEpoch, HeadKind.Synced,
            api => api.GetAttesterDuties(HeadEpoch - 2, [0], CancellationToken.None).AsTask(), BeaconNodeApiError.InvalidRequest);
        yield return Refused("Attester duties a lagging head cannot know", nextEpochStart, AttestationTimeMs, Presets.FarFutureEpoch, HeadKind.Synced,
            api => api.GetAttesterDuties(HeadEpoch + 2, [0], CancellationToken.None).AsTask(), BeaconNodeApiError.NotReady);
        yield return Refused("Proposer duties the lookahead dropped", HeadSlot, AttestationTimeMs, Presets.FarFutureEpoch, HeadKind.Synced,
            api => api.GetProposerDuties(HeadEpoch - 1, CancellationToken.None).AsTask(), BeaconNodeApiError.InvalidRequest);
        yield return Refused("Proposer duties a lagging head cannot know", nextEpochStart, AttestationTimeMs, Presets.FarFutureEpoch, HeadKind.Synced,
            api => api.GetProposerDuties(HeadEpoch + 2, CancellationToken.None).AsTask(), BeaconNodeApiError.NotReady);
        yield return Refused("Sync duties two periods ahead", HeadSlot, AttestationTimeMs, Presets.FarFutureEpoch, HeadKind.Synced,
            api => api.GetSyncDuties((SyncPeriod + 2) * Presets.EpochsPerSyncCommitteePeriod, [0], CancellationToken.None).AsTask(), BeaconNodeApiError.InvalidRequest);
        yield return Refused("Sync duties of a past period", HeadSlot, AttestationTimeMs, Presets.FarFutureEpoch, HeadKind.Synced,
            api => api.GetSyncDuties((SyncPeriod - 1) * Presets.EpochsPerSyncCommitteePeriod, [0], CancellationToken.None).AsTask(), BeaconNodeApiError.InvalidRequest);
        yield return Refused("Duties of a Gloas epoch", HeadSlot, AttestationTimeMs, HeadEpoch + 1, HeadKind.Synced,
            api => api.GetAttesterDuties(HeadEpoch + 1, [0], CancellationToken.None).AsTask(), BeaconNodeApiError.NotSupported);
        yield return Refused("Duties on a Gloas head", HeadSlot, AttestationTimeMs, Presets.FarFutureEpoch, HeadKind.Gloas,
            api => api.GetProposerDuties(HeadEpoch, CancellationToken.None).AsTask(), BeaconNodeApiError.NotSupported);
        yield return Refused("Attestation data while the head lags", HeadSlot + 2, AttestationTimeMs, Presets.FarFutureEpoch, HeadKind.Synced,
            api => api.ProduceAttestationData(HeadSlot + 2, 0, CancellationToken.None).AsTask(), BeaconNodeApiError.NotReady);
        yield return Refused("Attestation data on an optimistic head", HeadSlot, AttestationTimeMs, Presets.FarFutureEpoch, HeadKind.Optimistic,
            api => api.ProduceAttestationData(HeadSlot, 0, CancellationToken.None).AsTask(), BeaconNodeApiError.NotReady);
        yield return Refused("Attestation data for a slot other than the current one", HeadSlot, AttestationTimeMs, Presets.FarFutureEpoch, HeadKind.Synced,
            api => api.ProduceAttestationData(HeadSlot + 1, 0, CancellationToken.None).AsTask(), BeaconNodeApiError.InvalidRequest);
        yield return Refused("Attestation data for a slot before the head", HeadSlot, GossipRouter.MaximumGossipClockDisparityMs, Presets.FarFutureEpoch, HeadKind.Synced,
            api => api.ProduceAttestationData(HeadSlot - 1, 0, CancellationToken.None).AsTask(), BeaconNodeApiError.InvalidRequest);
        yield return Refused("Attestation data in a Gloas epoch", HeadSlot, AttestationTimeMs, HeadEpoch, HeadKind.Synced,
            api => api.ProduceAttestationData(HeadSlot, 0, CancellationToken.None).AsTask(), BeaconNodeApiError.NotSupported);
        yield return Refused("Attestations of a Gloas slot", HeadSlot, AttestationTimeMs, HeadEpoch, HeadKind.Synced,
            api => api.SubmitAttestations([new SingleAttestation { Data = new AttestationData { Slot = HeadSlot } }], CancellationToken.None).AsTask(), BeaconNodeApiError.NotSupported);

        static TestCaseData Refused(string name, ulong wallSlot, long millisecondsIntoSlot, ulong gloasForkEpoch, HeadKind head, Func<IBeaconNodeValidatorApi, Task> call, BeaconNodeApiError expected) =>
            new TestCaseData(wallSlot, millisecondsIntoSlot, gloasForkEpoch, head, call, expected).SetName(name);
    }

    [TestCaseSource(nameof(Refusals))]
    public async Task Calls_the_head_cannot_answer_are_refused(ulong wallSlot, long millisecondsIntoSlot, ulong gloasForkEpoch, HeadKind head, Func<IBeaconNodeValidatorApi, Task> call, BeaconNodeApiError expected)
    {
        using ValidatorNode node = new(gloasForkEpoch, wallSlot, millisecondsIntoSlot);
        node.Heads.Current = head == HeadKind.Gloas
            ? Head(HeadState(HeadSlot), HeadRoot, executionInSync: true) with { State = new ForkedBeaconState.OfGloas(new BeaconStateGloas()) }
            : Head(HeadState(HeadSlot), HeadRoot, executionInSync: head == HeadKind.Synced);

        Assert.That(await Refusal(() => call(node.Api)), Is.EqualTo(expected));
    }

    [TestCase(Flaw.None, null)]
    [TestCase(Flaw.NonZeroIndex, "index is not zero")]
    [TestCase(Flaw.FutureSlot, "is in the future")]
    [TestCase(Flaw.TargetEpoch, "is not the epoch")]
    [TestCase(Flaw.UnknownBlock, "is not known")]
    [TestCase(Flaw.CommitteeIndex, "is out of range")]
    [TestCase(Flaw.NotInCommittee, "is not in committee")]
    [TestCase(Flaw.Signature, "signature is invalid")]
    [TestCase(Flaw.TargetNotAncestor, "is not the checkpoint block")]
    [TestCase(Flaw.NotFinalizedDescendant, "does not descend from the finalized checkpoint")]
    public async Task Submitted_attestation_is_published_on_its_subnet_and_queued_for_fork_choice_unless_a_gossip_check_refuses_it(Flaw flaw, string? refusal)
    {
        using ValidatorNode node = new(Presets.FarFutureEpoch, HeadSlot, AttestationTimeMs);
        BeaconStateFulu state = HeadState(HeadSlot);
        node.Heads.Current = Head(state, HeadRoot, executionInSync: true);
        int[] committee = CommitteeCache.Build(state, HeadEpoch).GetBeaconCommittee(HeadSlot, 0).ToArray();
        SingleAttestation valid = await Attest(committee[0]);
        SingleAttestation submitted = await Attest(committee[1]);
        AttestationData data = submitted.Data!;
        switch (flaw)
        {
            case Flaw.NonZeroIndex: data.Index = 1; break;
            case Flaw.FutureSlot: data.Slot += 2; break;
            case Flaw.TargetEpoch: data.Target!.Epoch -= 1; break;
            case Flaw.UnknownBlock: data.BeaconBlockRoot = Keccak.Compute("unknown block"); break;
            case Flaw.CommitteeIndex: submitted.CommitteeIndex = 1; break;
            case Flaw.NotInCommittee: submitted.AttesterIndex = (ulong)Enumerable.Range(0, ValidatorCount).First(index => !committee.Contains(index)); break;
            case Flaw.TargetNotAncestor: data.BeaconBlockRoot = SiblingRoot; break;
            case Flaw.NotFinalizedDescendant: data.BeaconBlockRoot = data.Target!.Root = OrphanRoot; break;
        }
        submitted.Signature = Sign(state, flaw == Flaw.Signature ? committee[0] : (int)submitted.AttesterIndex, data);

        IReadOnlyList<SubmissionFailure> failures = await node.Api.SubmitAttestations([valid, submitted], CancellationToken.None);

        // One committee a slot at this registry size, so compute_subnet_for_attestation is the slot's position in its epoch.
        string topic = GossipTopics.Topic(ForkDigest.Compute(node.Spec, HeadEpoch), $"beacon_attestation_{HeadSlot % Presets.SlotsPerEpoch}");
        SingleAttestation[] published = refusal is null ? [valid, submitted] : [valid];
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(failures.Select(static failure => failure.Index), Is.EqualTo(refusal is null ? Array.Empty<int>() : [1]));
        if (refusal is not null) Assert.That(failures[0].Message, Does.Contain(refusal));
        Assert.That(node.Published, Is.EqualTo(published.Select(attestation => (topic, Snappy.CompressToArray(SingleAttestation.Encode(attestation))))));
        Assert.That(node.Orchestrator.QueuedVoteCount, Is.EqualTo(published.Length), "each published attestation waits for fork choice on the worker");

        async Task<SingleAttestation> Attest(int validatorIndex)
        {
            AttestationData produced = await node.Api.ProduceAttestationData(HeadSlot, 0, CancellationToken.None);
            return new SingleAttestation { CommitteeIndex = 0, AttesterIndex = (ulong)validatorIndex, Data = produced, Signature = Sign(state, validatorIndex, produced) };
        }
    }

    [Test]
    public async Task Members_of_later_duties_refuse_as_not_supported()
    {
        using ValidatorNode node = new(Presets.FarFutureEpoch, HeadSlot, AttestationTimeMs);
        IBeaconNodeValidatorApi api = node.Api;
        Func<Task>[] calls =
        [
            () => api.SubscribeToBeaconCommittees([], CancellationToken.None).AsTask(),
            () => api.GetAggregateAttestation(HeadSlot, HeadRoot, 0, CancellationToken.None).AsTask(),
            () => api.SubmitAggregateAndProofs([], CancellationToken.None).AsTask(),
            () => api.PrepareBeaconProposers([], CancellationToken.None).AsTask(),
            () => api.ProduceBlock(HeadSlot, default, HeadRoot, CancellationToken.None).AsTask(),
            () => api.PublishBlock(new SignedBlockContents(new SignedBeaconBlock(), [], []), CancellationToken.None).AsTask(),
            () => api.SubscribeToSyncCommittees([], CancellationToken.None).AsTask(),
            () => api.SubmitSyncCommitteeMessages([], CancellationToken.None).AsTask(),
            () => api.ProduceSyncCommitteeContribution(HeadSlot, 0, HeadRoot, CancellationToken.None).AsTask(),
            () => api.SubmitContributionAndProofs([], CancellationToken.None).AsTask(),
        ];

        BeaconNodeApiError?[] refusals = new BeaconNodeApiError?[calls.Length];
        for (int i = 0; i < calls.Length; i++) refusals[i] = await Refusal(calls[i]);

        Assert.That(refusals, Is.All.EqualTo(BeaconNodeApiError.NotSupported));
    }

    /// <summary>The post-state of a head block at <paramref name="slot"/>, on a chain with <see cref="ParentRoot"/> just before the head epoch and <see cref="BoundaryRoot"/> at its first slot.</summary>
    private static BeaconStateFulu HeadState(ulong slot)
    {
        BeaconStateFulu state = GloasTestFixtures.CreateFuluState(ValidatorCount);
        Validator[] validators = state.Validators!;
        GloasTestFixtures.InstallValidatorKeys(validators, ValidatorCount);
        state.Slot = slot;
        for (ulong past = slot - Presets.SlotsPerHistoricalRoot; past < slot; past++)
            state.BlockRoots![(int)(past % Presets.SlotsPerHistoricalRoot)] = BlockRootAt(past);
        state.ProposerLookahead = [.. Enumerable.Range(0, (int)Presets.ProposerLookaheadSlots).Select(static slotOffset => (ulong)(ValidatorCount - 1 - slotOffset))];
        state.CurrentJustifiedCheckpoint = new Checkpoint { Epoch = Finalized.Epoch, Root = Finalized.Root };
        state.CurrentSyncCommittee = Committee(static position => position % 3);
        state.NextSyncCommittee = Committee(static position => 3 + position % 2);
        return state;

        SyncCommittee Committee(Func<int, int> member) => new()
        {
            Pubkeys = [.. Enumerable.Range(0, Presets.SyncCommitteeSize).Select(position => validators[member(position)].Pubkey)],
            AggregatePubkey = GloasTestFixtures.Pubkey(0x60),
        };
    }

    private static Hash256 BlockRootAt(ulong slot) => slot >= EpochStart ? BoundaryRoot : slot == EpochStart - 1 ? ParentRoot : Keccak.Compute(slot.ToString());

    /// <summary>beacon-APIs: <c>get_block_root_at_slot(state, compute_start_slot_at_epoch(epoch - 1) - 1)</c>.</summary>
    private static Hash256 DependentRoot(ulong epoch) => BlockRootAt(BeaconStateAccessors.ComputeStartSlotAtEpoch(epoch - 1) - 1);

    private static HeadSnapshot Head(BeaconStateFulu state, Hash256 root, bool executionInSync) =>
        new(new StatusMessageV2 { ForkDigest = [], HeadRoot = root, HeadSlot = state.Slot, FinalizedRoot = Finalized.Root, FinalizedEpoch = Finalized.Epoch }, null, Finalized.Root, executionInSync)
        {
            State = new ForkedBeaconState.OfFulu(state),
            ForkChoice = new ForkChoiceSnapshot(Finalized, Finalized, Hash256.Zero, Nodes),
        };

    private static ForkChoiceSnapshotNode Node(ulong slot, Hash256 root, Hash256? parent) => new(slot, root, parent, 0, 0, 0, ExecutionStatus.Valid, Keccak.Zero);

    private static BlsSignature Sign(BeaconStateFulu state, int validatorIndex, AttestationData data) =>
        GloasTestFixtures.Sign(GloasTestFixtures.ValidatorKey(validatorIndex),
            Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), state.GetDomain(DomainType.BeaconAttester, data.Target!.Epoch)));

    private static async Task<BeaconNodeApiError?> Refusal(Func<Task> call)
    {
        try
        {
            await call();
            return null;
        }
        catch (BeaconNodeApiException e)
        {
            return e.Error;
        }
    }

    /// <summary>The validator API as the container wires it, on a wall clock at <paramref name="millisecondsIntoSlot"/> into <paramref name="wallSlot"/>, recording what it publishes.</summary>
    private sealed class ValidatorNode : IDisposable
    {
        private readonly IContainer _container;

        public ValidatorNode(ulong gloasForkEpoch, ulong wallSlot, long millisecondsIntoSlot)
        {
            BeaconChainSpec spec = BeaconChainSpec.Mainnet with { GloasForkEpoch = gloasForkEpoch };
            ManualTimestamper clock = new(DateTimeOffset.FromUnixTimeMilliseconds((long)(spec.GenesisTime + wallSlot * spec.SecondsPerSlot) * 1000 + millisecondsIntoSlot).UtcDateTime);
            _container = BeaconChainTestContainer.Builder()
                .AddSingleton(spec)
                .AddSingleton<ITimestamper>(clock)
                .Build();
            Api = (BeaconNodeValidatorApi)_container.Resolve<IBeaconNodeValidatorApi>();
            Api.PublishGossip = (topic, message) => Published.Add((topic, message));
        }

        public BeaconNodeValidatorApi Api { get; }
        public List<(string Topic, byte[] Message)> Published { get; } = [];
        public BeaconChainSpec Spec => _container.Resolve<BeaconChainSpec>();
        public HeadSnapshotHolder Heads => _container.Resolve<HeadSnapshotHolder>();
        public BeaconSyncOrchestrator Orchestrator => _container.Resolve<BeaconSyncOrchestrator>();

        public void Dispose() => _container.Dispose();
    }
}
