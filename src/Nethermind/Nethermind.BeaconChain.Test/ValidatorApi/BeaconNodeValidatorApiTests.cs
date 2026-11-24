// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Net;
using Autofac;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Test.P2P;
using Nethermind.BeaconChain.Test.P2P.Gossip;
using Nethermind.BeaconChain.Test.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.BeaconChain.ValidatorApi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Protocols.Pubsub;
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
    private static readonly Hash256 OptimisticRoot = Keccak.Compute("block whose payload is not verified yet");

    private static readonly ForkChoiceSnapshotNode[] Nodes =
    [
        Node(BeaconStateAccessors.ComputeStartSlotAtEpoch(Finalized.Epoch), Finalized.Root, null),
        Node(EpochStart - 1, ParentRoot, Finalized.Root),
        Node(EpochStart, BoundaryRoot, ParentRoot),
        Node(EpochStart, SiblingRoot, ParentRoot),
        Node(HeadSlot, HeadRoot, BoundaryRoot),
        Node(EpochStart - 5, OrphanRoot, null),
        Node(HeadSlot, OptimisticRoot, BoundaryRoot) with { ExecutionStatus = ExecutionStatus.Optimistic },
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
        yield return Refused("Sync committee messages of a Gloas slot", HeadSlot, AttestationTimeMs, HeadEpoch, HeadKind.Synced,
            api => api.SubmitSyncCommitteeMessages([new SyncCommitteeMessage { Slot = HeadSlot, BeaconBlockRoot = HeadRoot }], CancellationToken.None).AsTask(), BeaconNodeApiError.NotSupported);
        yield return Refused("Sync committee contribution of a Gloas slot", HeadSlot, AttestationTimeMs, HeadEpoch, HeadKind.Synced,
            api => api.ProduceSyncCommitteeContribution(HeadSlot, 0, HeadRoot, CancellationToken.None).AsTask(), BeaconNodeApiError.NotSupported);
        yield return Refused("Contributions of a Gloas slot", HeadSlot, AttestationTimeMs, HeadEpoch, HeadKind.Synced,
            api => api.SubmitContributionAndProofs([new SignedContributionAndProof { Message = new ContributionAndProof { Contribution = new SyncCommitteeContribution { Slot = HeadSlot } } }],
                CancellationToken.None).AsTask(), BeaconNodeApiError.NotSupported);
        yield return Refused("Sync committee contribution of a subcommittee past the last", HeadSlot, AttestationTimeMs, Presets.FarFutureEpoch, HeadKind.Synced,
            api => api.ProduceSyncCommitteeContribution(HeadSlot, SyncCommitteeContribution.SubnetCount, HeadRoot, CancellationToken.None).AsTask(), BeaconNodeApiError.InvalidRequest);
        yield return Refused("Sync committee contribution for an optimistic block", HeadSlot, AttestationTimeMs, Presets.FarFutureEpoch, HeadKind.Synced,
            api => api.ProduceSyncCommitteeContribution(HeadSlot, 0, OptimisticRoot, CancellationToken.None).AsTask(), BeaconNodeApiError.NotReady);
        yield return Refused("Sync committee contribution of no message held", HeadSlot, AttestationTimeMs, Presets.FarFutureEpoch, HeadKind.Synced,
            api => api.ProduceSyncCommitteeContribution(HeadSlot, 0, HeadRoot, CancellationToken.None).AsTask(), BeaconNodeApiError.NotFound);

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
            () => api.PrepareBeaconProposers([], CancellationToken.None).AsTask(),
            () => api.ProduceBlock(HeadSlot, default, HeadRoot, CancellationToken.None).AsTask(),
            () => api.PublishBlock(new SignedBlockContents(new SignedBeaconBlock(), [], []), CancellationToken.None).AsTask(),
        ];

        BeaconNodeApiError?[] refusals = new BeaconNodeApiError?[calls.Length];
        for (int i = 0; i < calls.Length; i++) refusals[i] = await Refusal(calls[i]);

        Assert.That(refusals, Is.All.EqualTo(BeaconNodeApiError.NotSupported));
    }

    public enum GossipFlaw { None, AlreadySeen, NonZeroIndex, UnknownBlock, FailedBlock, CommitteeIndex, WrongSubnet, FutureSlot, StaleEpoch, TargetEpoch, NotInCommittee, Signature, TargetNotAncestor, NotFinalizedDescendant, UnjoinedSubnet }

    // electra/p2p-interface.md beacon_attestation_{subnet_id}: each case breaks one rule, and gets that rule's verdict.
    [TestCase(GossipFlaw.None, MessageValidity.Accepted)]
    [TestCase(GossipFlaw.AlreadySeen, MessageValidity.Ignored)]
    [TestCase(GossipFlaw.NonZeroIndex, MessageValidity.Rejected)]
    [TestCase(GossipFlaw.UnknownBlock, MessageValidity.Ignored)]
    [TestCase(GossipFlaw.FailedBlock, MessageValidity.Rejected)]
    [TestCase(GossipFlaw.CommitteeIndex, MessageValidity.Rejected)]
    [TestCase(GossipFlaw.WrongSubnet, MessageValidity.Rejected)]
    [TestCase(GossipFlaw.FutureSlot, MessageValidity.Ignored)]
    [TestCase(GossipFlaw.StaleEpoch, MessageValidity.Ignored)]
    [TestCase(GossipFlaw.TargetEpoch, MessageValidity.Rejected)]
    [TestCase(GossipFlaw.NotInCommittee, MessageValidity.Rejected)]
    [TestCase(GossipFlaw.Signature, MessageValidity.Rejected)]
    [TestCase(GossipFlaw.TargetNotAncestor, MessageValidity.Rejected)]
    [TestCase(GossipFlaw.NotFinalizedDescendant, MessageValidity.Ignored)]
    [TestCase(GossipFlaw.UnjoinedSubnet, MessageValidity.Ignored)]
    public async Task Gossip_attestation_on_a_joined_subnet_gets_the_verdict_of_the_rule_it_breaks_and_only_an_accepted_one_is_voted_and_pooled(GossipFlaw flaw, MessageValidity expected)
    {
        using ValidatorNode node = new(Presets.FarFutureEpoch, HeadSlot, AttestationTimeMs);
        BeaconStateFulu state = HeadState(HeadSlot);
        node.Heads.Current = Head(state, HeadRoot, executionInSync: true);
        int[] committee = CommitteeCache.Build(state, HeadEpoch).GetBeaconCommittee(HeadSlot, 0).ToArray();
        ulong subnet = HeadSlot % Presets.SlotsPerEpoch;
        // The slot's subnet, the next one, and the subnet of the slot after that, for the future-slot case.
        node.Gossip.SetAttestationSubnets(0b111UL << (int)subnet);
        node.Orchestrator.RouteGossipEvents();
        AttestationData data = await node.Api.ProduceAttestationData(HeadSlot, 0, CancellationToken.None);
        SingleAttestation attestation = new() { CommitteeIndex = 0, AttesterIndex = (ulong)committee[1], Data = data };
        switch (flaw)
        {
            case GossipFlaw.AlreadySeen:
                AttestationData boundaryVote = await node.Api.ProduceAttestationData(HeadSlot, 0, CancellationToken.None);
                boundaryVote.BeaconBlockRoot = BoundaryRoot;
                await Deliver(node, subnet, new SingleAttestation { CommitteeIndex = 0, AttesterIndex = attestation.AttesterIndex, Data = boundaryVote, Signature = Sign(state, committee[1], boundaryVote) });
                break;
            case GossipFlaw.NonZeroIndex: data.Index = 1; break;
            case GossipFlaw.UnknownBlock: data.BeaconBlockRoot = Keccak.Compute("unknown block"); break;
            case GossipFlaw.FailedBlock: data.BeaconBlockRoot = Keccak.Compute("failed block"); node.FailedBlocks.Add(data.BeaconBlockRoot, HeadSlot); break;
            case GossipFlaw.CommitteeIndex: attestation.CommitteeIndex = 1; break;
            case GossipFlaw.FutureSlot: data.Slot += 2; break;
            case GossipFlaw.StaleEpoch: node.Clock.Add(TimeSpan.FromSeconds(2 * Presets.SlotsPerEpoch * node.Spec.SecondsPerSlot)); break;
            case GossipFlaw.TargetEpoch: data.Target!.Epoch -= 1; break;
            case GossipFlaw.NotInCommittee: attestation.AttesterIndex = (ulong)Enumerable.Range(0, ValidatorCount).First(index => !committee.Contains(index)); break;
            case GossipFlaw.TargetNotAncestor: data.BeaconBlockRoot = SiblingRoot; break;
            case GossipFlaw.NotFinalizedDescendant: data.BeaconBlockRoot = data.Target!.Root = OrphanRoot; break;
        }
        attestation.Signature = Sign(state, flaw == GossipFlaw.Signature ? committee[0] : (int)attestation.AttesterIndex, data);
        int queuedBefore = node.Orchestrator.QueuedVoteCount;

        MessageValidity validity = await Deliver(node, flaw switch { GossipFlaw.WrongSubnet => subnet + 1, GossipFlaw.UnjoinedSubnet => subnet + 3, _ => data.Slot % Presets.SlotsPerEpoch }, attestation);

        bool accepted = expected == MessageValidity.Accepted;
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(validity, Is.EqualTo(expected));
        Assert.That(node.Orchestrator.QueuedVoteCount - queuedBefore, Is.EqualTo(accepted ? 1 : 0), "an accepted vote waits for fork choice on the worker");
        Assert.That(node.Pool.GetAggregate(data.Slot, SszRoots.HashTreeRoot(data), attestation.CommitteeIndex)?.AggregationBits, accepted ? Is.EqualTo(new BitArray(committee.Length) { [1] = true }) : Is.Null);
    }

    [Test]
    public async Task A_vote_seen_on_gossip_is_ignored_when_relayed_again_yet_accepted_and_published_when_the_validator_submits_it()
    {
        using ValidatorNode node = new(Presets.FarFutureEpoch, HeadSlot, AttestationTimeMs);
        BeaconStateFulu state = HeadState(HeadSlot);
        node.Heads.Current = Head(state, HeadRoot, executionInSync: true);
        int validator = CommitteeCache.Build(state, HeadEpoch).GetBeaconCommittee(HeadSlot, 0)[0];
        ulong subnet = HeadSlot % Presets.SlotsPerEpoch;
        node.Gossip.SetAttestationSubnets(1UL << (int)subnet);
        AttestationData data = await node.Api.ProduceAttestationData(HeadSlot, 0, CancellationToken.None);
        AttestationData otherHead = await node.Api.ProduceAttestationData(HeadSlot, 0, CancellationToken.None);
        otherHead.BeaconBlockRoot = BoundaryRoot;
        SingleAttestation vote = new() { CommitteeIndex = 0, AttesterIndex = (ulong)validator, Data = data, Signature = Sign(state, validator, data) };

        MessageValidity first = await Deliver(node, subnet, vote);
        MessageValidity other = await Deliver(node, subnet, new SingleAttestation { CommitteeIndex = 0, AttesterIndex = (ulong)validator, Data = otherHead, Signature = Sign(state, validator, otherHead) });
        IReadOnlyList<SubmissionFailure> failures = await node.Api.SubmitAttestations([vote], CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That((first, other), Is.EqualTo((MessageValidity.Accepted, MessageValidity.Ignored)), "a second vote of the validator for the target epoch is ignored on gossip");
        Assert.That(failures, Is.Empty);
        Assert.That(node.Published.Select(static published => published.Message), Is.EqualTo(new[] { Snappy.CompressToArray(SingleAttestation.Encode(vote)) }));
    }

    [Test]
    public async Task Attestations_of_one_vote_are_served_as_one_aggregate_of_every_distinct_attester_until_their_slot_is_two_slots_old()
    {
        using ValidatorNode node = new(Presets.FarFutureEpoch, HeadSlot, AttestationTimeMs);
        BeaconStateFulu state = HeadState(HeadSlot);
        node.Heads.Current = Head(state, HeadRoot, executionInSync: true);
        int[] committee = CommitteeCache.Build(state, HeadEpoch).GetBeaconCommittee(HeadSlot, 0).ToArray();
        AttestationData data = await node.Api.ProduceAttestationData(HeadSlot, 0, CancellationToken.None);
        AttestationData otherHead = await node.Api.ProduceAttestationData(HeadSlot, 0, CancellationToken.None);
        otherHead.BeaconBlockRoot = BoundaryRoot;
        SingleAttestation[] votes = [Vote(committee[1], data), Vote(committee[0], data), Vote(committee[1], data), Vote(committee[0], otherHead)];
        Hash256 dataRoot = SszRoots.HashTreeRoot(data);

        IReadOnlyList<SubmissionFailure> failures = await node.Api.SubmitAttestations(votes, CancellationToken.None);
        Attestation aggregate = await node.Api.GetAggregateAttestation(HeadSlot, dataRoot, 0, CancellationToken.None);
        Attestation otherAggregate = await node.Api.GetAggregateAttestation(HeadSlot, SszRoots.HashTreeRoot(otherHead), 0, CancellationToken.None);
        BeaconNodeApiError? otherCommittee = await Refusal(() => node.Api.GetAggregateAttestation(HeadSlot, dataRoot, 1, CancellationToken.None).AsTask());
        node.Clock.Add(TimeSpan.FromSeconds(2 * node.Spec.SecondsPerSlot));
        BeaconNodeApiError? pruned = await Refusal(() => node.Api.GetAggregateAttestation(HeadSlot, dataRoot, 0, CancellationToken.None).AsTask());

        Hash256 signingRoot = Domains.ComputeSigningRoot(dataRoot, state.GetDomain(DomainType.BeaconAttester, HeadEpoch));
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(failures, Is.Empty, "a repeated vote is the validator's own, so it is accepted and counted once");
        Assert.That(aggregate.AggregationBits, Is.EqualTo(new BitArray(committee.Length) { [0] = true, [1] = true }));
        Assert.That(aggregate.CommitteeBits, Is.EqualTo(new BitArray(Presets.MaxCommitteesPerSlot) { [0] = true }));
        Assert.That(aggregate.Data, Is.SameAs(data));
        Assert.That(aggregate.Signature, Is.EqualTo(GloasTestFixtures.AggregateSignature(signingRoot, [committee[1], committee[0]])));
        Assert.That(otherAggregate.AggregationBits, Is.EqualTo(new BitArray(committee.Length) { [0] = true }), "another head vote is another aggregate");
        Assert.That((otherCommittee, pruned), Is.EqualTo(((BeaconNodeApiError?)BeaconNodeApiError.NotFound, (BeaconNodeApiError?)BeaconNodeApiError.NotFound)));

        SingleAttestation Vote(int validator, AttestationData voted) => new() { CommitteeIndex = 0, AttesterIndex = (ulong)validator, Data = voted, Signature = Sign(state, validator, voted) };
    }

    [Test]
    public async Task Committee_subscriptions_join_an_aggregators_subnet_shortly_before_its_slot_until_it_passed_and_advertise_it()
    {
        using ValidatorNode node = new(Presets.FarFutureEpoch, HeadSlot, AttestationTimeMs);
        node.Store.EnsureSchemaVersion();
        Dictionary<string, GossipDigestWindowTests.RecordingTopic> topics = [];
        byte[] digest = ForkDigest.Compute(node.Spec, HeadEpoch);
        node.Gossip.Start(topicId => topics.TryGetValue(topicId, out GossipDigestWindowTests.RecordingTopic? topic) ? topic : topics[topicId] = new GossipDigestWindowTests.RecordingTopic(), digest);
        node.Discovery.CreateDiscv5Services(IPAddress.Loopback);
        ulong sequence = node.Metadata.Current.SeqNumber;
        // One committee a slot, so each slot's subnet is its position in the epoch.
        BeaconCommitteeSubscription soon = new(1, 0, 1, HeadSlot + 1, IsAggregator: true);
        BeaconCommitteeSubscription later = new(2, 0, 1, HeadSlot + 4, IsAggregator: true);
        BeaconCommitteeSubscription attestOnly = new(3, 0, 1, HeadSlot + 2, IsAggregator: false);

        BeaconNodeApiError? beyondNextEpoch = await Refusal(() => node.Api.SubscribeToBeaconCommittees(
            [later, new BeaconCommitteeSubscription(4, 0, 1, BeaconStateAccessors.ComputeStartSlotAtEpoch(HeadEpoch + 2), IsAggregator: true)], CancellationToken.None).AsTask());
        BeaconNodeApiError? committeeOutOfRange = await Refusal(() => node.Api.SubscribeToBeaconCommittees([new BeaconCommitteeSubscription(4, 1, 1, HeadSlot + 1, IsAggregator: true)], CancellationToken.None).AsTask());
        (ulong[] Topics, ulong[] Metadata, ulong[] Enr, ulong Sequence) refused = Advertised(node, topics, digest);
        await node.Api.SubscribeToBeaconCommittees([soon, later, attestOnly], CancellationToken.None);
        (ulong[], ulong[], ulong[], ulong) subscribed = Advertised(node, topics, digest);
        // A digest subscribed later, here the next BPO one, joins the same subnets.
        byte[] laterDigest = GossipTopics.NextRotation(node.Spec, HeadEpoch)!.Value.Digest;
        node.Gossip.SubscribeDigest(laterDigest);
        ulong[] onLaterDigest = Advertised(node, topics, laterDigest).Topics;
        node.Subnets.Update(HeadSlot + 2);
        (ulong[], ulong[], ulong[], ulong) afterFirstSlot = Advertised(node, topics, digest);
        node.Subnets.Update(HeadSlot + 5);
        (ulong[], ulong[], ulong[], ulong) afterLastSlot = Advertised(node, topics, digest);
        ulong[] onLaterDigestAfterLastSlot = Advertised(node, topics, laterDigest).Topics;

        ulong[] soonSubnet = [(HeadSlot + 1) % Presets.SlotsPerEpoch];
        ulong[] laterSubnet = [(HeadSlot + 4) % Presets.SlotsPerEpoch];
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That((beyondNextEpoch, committeeOutOfRange), Is.EqualTo(((BeaconNodeApiError?)BeaconNodeApiError.InvalidRequest, (BeaconNodeApiError?)BeaconNodeApiError.InvalidRequest)));
        Assert.That(refused, Is.EqualTo((Array.Empty<ulong>(), Array.Empty<ulong>(), Array.Empty<ulong>(), sequence)), "a refused batch subscribes nothing");
        Assert.That(subscribed, Is.EqualTo((soonSubnet, soonSubnet, soonSubnet, sequence + 1)), "only the aggregation within the lead is joined; an attester publishes by fanout");
        Assert.That((onLaterDigest, onLaterDigestAfterLastSlot), Is.EqualTo((soonSubnet, Array.Empty<ulong>())));
        Assert.That(afterFirstSlot, Is.EqualTo((laterSubnet, laterSubnet, laterSubnet, sequence + 2)));
        Assert.That(afterLastSlot, Is.EqualTo((Array.Empty<ulong>(), Array.Empty<ulong>(), Array.Empty<ulong>(), sequence + 3)), "an empty bitfield leaves the ENR entry out");
        Assert.That(node.Heads.CaptureState, Is.True, "checking a joined subnet's attestations needs the head state");
    }

    private static IEnumerable<TestCaseData> AggregateVerdicts()
    {
        yield return new TestCaseData(MessageValidity.Accepted, null).SetName("Aggregate the worker accepts is published");
        yield return new TestCaseData(MessageValidity.Rejected, "fails a beacon_aggregate_and_proof gossip rule").SetName("Aggregate the worker rejects is refused");
        yield return new TestCaseData(MessageValidity.Ignored, "cannot be checked now").SetName("Aggregate the worker ignores is refused");
        yield return new TestCaseData(MessageValidity.Throttled, "too busy").SetName("Aggregate the full vote queue throttles is refused");
    }

    [TestCaseSource(nameof(AggregateVerdicts))]
    public async Task Submitted_aggregates_get_the_gossip_verdict_and_only_an_accepted_one_is_published(MessageValidity workerVerdict, string? refusal)
    {
        using ValidatorNode node = new(Presets.FarFutureEpoch, HeadSlot, AttestationTimeMs);
        node.Heads.Current = Head(HeadState(HeadSlot), HeadRoot, executionInSync: true);
        node.Store.PutBlock(HeadRoot, TestChain.CreateBlock(HeadSlot, BoundaryRoot));
        AttestationData data = await node.Api.ProduceAttestationData(HeadSlot, 0, CancellationToken.None);
        // The seen aggregate covers another member, so it does not cover the one checked on the worker.
        SignedAggregateAndProof checkedOnWorker = Aggregate(aggregator: 1, member: 0, data), twoCommittees = Aggregate(aggregator: 2, member: 0, data), alreadySeen = Aggregate(aggregator: 3, member: 1, data);
        twoCommittees.Message!.Aggregate!.CommitteeBits![1] = true;
        node.Gossip.MarkAggregateSeen(data, alreadySeen.Message!.Aggregate!.CommitteeBits!, alreadySeen.Message.Aggregate.AggregationBits!, 3);
        List<ulong> checkedAggregators = [];
        node.Gossip.AggregateAndProofReceived += (aggregate, verdict) =>
        {
            checkedAggregators.Add(aggregate.Message!.AggregatorIndex);
            verdict.Complete(workerVerdict);
        };

        BeaconNodeApiError? beforeGossip = await Refusal(() => node.Api.SubmitAggregateAndProofs([checkedOnWorker], CancellationToken.None).AsTask());
        node.Orchestrator.GossipStarted = true;
        IReadOnlyList<SubmissionFailure> failures = await node.Api.SubmitAggregateAndProofs([checkedOnWorker, twoCommittees, alreadySeen], CancellationToken.None);

        string topic = GossipTopics.Topic(ForkDigest.Compute(node.Spec, HeadEpoch), GossipTopics.BeaconAggregateAndProof);
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(beforeGossip, Is.EqualTo(BeaconNodeApiError.NotReady));
        Assert.That(checkedAggregators, Is.EqualTo(new[] { 1UL }), "the stateless rules refuse two committees before the worker, and a seen aggregate is not checked");
        Assert.That(failures.Select(static failure => failure.Index), Is.EqualTo(refusal is null ? new[] { 1 } : [0, 1]), "a seen aggregate is no failure");
        Assert.That(failures.Count > 1 ? failures[0].Message : null, refusal is null ? Is.Null : Does.Contain(refusal));
        Assert.That(node.Published, Is.EqualTo(refusal is null ? new[] { (topic, Snappy.CompressToArray(SignedAggregateAndProof.Encode(checkedOnWorker))) } : []));

        static SignedAggregateAndProof Aggregate(ulong aggregator, int member, AttestationData data) => new()
        {
            Message = new AggregateAndProof
            {
                AggregatorIndex = aggregator,
                Aggregate = new Attestation
                {
                    AggregationBits = new BitArray(2) { [member] = true },
                    Data = data,
                    CommitteeBits = new BitArray(Presets.MaxCommitteesPerSlot) { [0] = true },
                },
            },
        };
    }

    public enum SyncFlaw { None, FutureSlot, StaleSlot, ValidatorIndex, NotInCommittee, Signature }

    [TestCase(SyncFlaw.None, null)]
    [TestCase(SyncFlaw.FutureSlot, "is in the future")]
    [TestCase(SyncFlaw.StaleSlot, "has passed")]
    [TestCase(SyncFlaw.ValidatorIndex, "is out of range")]
    [TestCase(SyncFlaw.NotInCommittee, "is not in the sync committee")]
    [TestCase(SyncFlaw.Signature, "signature is invalid")]
    public async Task Submitted_sync_committee_message_is_published_on_every_subnet_of_its_validator_and_counted_at_each_position_unless_a_gossip_check_refuses_it(SyncFlaw flaw, string? refusal)
    {
        using ValidatorNode node = new(Presets.FarFutureEpoch, HeadSlot, AttestationTimeMs);
        BeaconStateFulu state = SyncCommitteeHeadState(HeadSlot);
        node.Heads.Current = Head(state, HeadRoot, executionInSync: true);
        node.Gossip.SetSyncCommitteeSubnets(1);
        SyncCommitteeMessage everySubnet = SyncMessage(state, 0, HeadSlot, HeadRoot);
        SyncCommitteeMessage submitted = SyncMessage(state, 2, HeadSlot, HeadRoot);
        switch (flaw)
        {
            case SyncFlaw.FutureSlot: submitted.Slot += 2; break;
            case SyncFlaw.StaleSlot: submitted.Slot -= 2; break;
            case SyncFlaw.ValidatorIndex: submitted.ValidatorIndex = 1000; break;
            case SyncFlaw.NotInCommittee: submitted.ValidatorIndex = 40; break;
        }
        submitted.Signature = SyncMessageSignature(state, flaw == SyncFlaw.Signature ? 3 : (int)submitted.ValidatorIndex, submitted.Slot, HeadRoot);

        // Seen on gossip first, which a local submission does not count against it.
        MessageValidity gossiped = await Deliver(node, GossipTopics.SyncCommitteeTopicNames[0], SyncCommitteeMessage.Encode(everySubnet));
        IReadOnlyList<SubmissionFailure> failures = await node.Api.SubmitSyncCommitteeMessages([everySubnet, submitted], CancellationToken.None);
        SyncCommitteeContribution first = await node.Api.ProduceSyncCommitteeContribution(HeadSlot, 0, HeadRoot, CancellationToken.None);
        SyncCommitteeContribution second = await node.Api.ProduceSyncCommitteeContribution(HeadSlot, 1, HeadRoot, CancellationToken.None);

        byte[] digest = ForkDigest.Compute(node.Spec, HeadEpoch);
        bool accepted = refusal is null;
        (string, byte[])[] published =
        [
            .. GossipTopics.SyncCommitteeTopicNames.Select(name => (GossipTopics.Topic(digest, name), Snappy.CompressToArray(SyncCommitteeMessage.Encode(everySubnet)))),
            .. accepted ? new[] { (GossipTopics.Topic(digest, GossipTopics.SyncCommitteeTopicNames[0]), Snappy.CompressToArray(SyncCommitteeMessage.Encode(submitted))) } : [],
        ];
        // Validator 0 holds positions 0, 32, 64 and 96 of subcommittee 0, validator 2 positions 2, 34, 66 and 98, and validator 0 the even ones of subcommittee 1.
        int[] firstPositions = accepted ? [0, 2, 32, 34, 64, 66, 96, 98] : [0, 32, 64, 96];
        int[] secondPositions = [.. Enumerable.Range(0, SyncCommitteeContribution.SubcommitteeSize / 2).Select(static half => 2 * half)];
        Hash256 signingRoot = SyncCommitteeSigningRoot(state, HeadSlot, HeadRoot);
        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(gossiped, Is.EqualTo(MessageValidity.Accepted));
        Assert.That(failures.Select(static failure => failure.Index), Is.EqualTo(accepted ? Array.Empty<int>() : [1]));
        if (!accepted) Assert.That(failures[0].Message, Does.Contain(refusal));
        Assert.That(node.Published, Is.EqualTo(published));
        Assert.That(first.AggregationBits, Is.EqualTo(Bits(firstPositions)));
        Assert.That(first.Signature, Is.EqualTo(GloasTestFixtures.AggregateSignature(signingRoot, [.. firstPositions.Select(static position => position % 32)])));
        Assert.That((second.Slot, second.BeaconBlockRoot, second.SubcommitteeIndex, second.AggregationBits), Is.EqualTo((HeadSlot, HeadRoot, 1UL, Bits(secondPositions))));
        Assert.That(second.Signature, Is.EqualTo(GloasTestFixtures.AggregateSignature(signingRoot, [.. secondPositions.Select(static _ => 0)])), "one signature for each position held");
    }

    [Test]
    public async Task A_message_for_the_last_slot_of_a_period_is_checked_against_the_next_committee_even_from_a_head_a_slot_behind([Values] bool headLags)
    {
        ulong lastSlot = BeaconStateAccessors.ComputeStartSlotAtEpoch((SyncPeriod + 1) * Presets.EpochsPerSyncCommitteePeriod) - 1;
        using ValidatorNode node = new(Presets.FarFutureEpoch, lastSlot, AttestationTimeMs);
        BeaconStateFulu state = HeadState(headLags ? lastSlot - 1 : lastSlot);
        node.Heads.Current = Head(state, HeadRoot, executionInSync: true);

        // HeadState puts validators 0, 1 and 2 in the current committee and 3 and 4 in the next, each in every subcommittee.
        IReadOnlyList<SubmissionFailure> failures = await node.Api.SubmitSyncCommitteeMessages([SyncMessage(state, 3, lastSlot, HeadRoot), SyncMessage(state, 0, lastSlot, HeadRoot)], CancellationToken.None);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(failures.Select(static failure => failure.Index), Is.EqualTo(new[] { 1 }));
        Assert.That(failures[0].Message, Does.Contain("is not in the sync committee"));
        Assert.That(node.Published, Has.Count.EqualTo(SyncCommitteeContribution.SubnetCount));
    }

    public enum SyncGossipFlaw { None, AlreadySeen, FutureSlot, ValidatorIndex, NotInCommittee, WrongSubnet, Signature, UnjoinedSubnet }

    // altair/p2p-interface.md sync_committee_{subnet_id}: each case breaks one rule, and gets that rule's verdict.
    [TestCase(SyncGossipFlaw.None, MessageValidity.Accepted)]
    [TestCase(SyncGossipFlaw.AlreadySeen, MessageValidity.Ignored)]
    [TestCase(SyncGossipFlaw.FutureSlot, MessageValidity.Ignored)]
    [TestCase(SyncGossipFlaw.ValidatorIndex, MessageValidity.Rejected)]
    [TestCase(SyncGossipFlaw.NotInCommittee, MessageValidity.Rejected)]
    [TestCase(SyncGossipFlaw.WrongSubnet, MessageValidity.Rejected)]
    [TestCase(SyncGossipFlaw.Signature, MessageValidity.Rejected)]
    [TestCase(SyncGossipFlaw.UnjoinedSubnet, MessageValidity.Ignored)]
    public async Task Gossip_sync_committee_message_on_a_joined_subnet_gets_the_verdict_of_the_rule_it_breaks_and_only_an_accepted_one_is_pooled(SyncGossipFlaw flaw, MessageValidity expected)
    {
        using ValidatorNode node = new(Presets.FarFutureEpoch, HeadSlot, AttestationTimeMs);
        BeaconStateFulu state = SyncCommitteeHeadState(HeadSlot);
        node.Heads.Current = Head(state, HeadRoot, executionInSync: true);
        node.Gossip.SetSyncCommitteeSubnets(0b0111);
        // Validator 2 is in subcommittee 0 only, validator 0 in every one.
        SyncCommitteeMessage message = SyncMessage(state, 2, HeadSlot, HeadRoot);
        ulong subnet = 0;
        switch (flaw)
        {
            case SyncGossipFlaw.AlreadySeen: await Deliver(node, GossipTopics.SyncCommitteeTopicNames[0], SyncCommitteeMessage.Encode(SyncMessage(state, 2, HeadSlot, BoundaryRoot))); break;
            case SyncGossipFlaw.FutureSlot: message.Slot += 2; break;
            case SyncGossipFlaw.ValidatorIndex: message.ValidatorIndex = 1000; break;
            case SyncGossipFlaw.NotInCommittee: message.ValidatorIndex = 40; break;
            case SyncGossipFlaw.WrongSubnet: subnet = 1; break;
            case SyncGossipFlaw.UnjoinedSubnet: message.ValidatorIndex = 0; subnet = 3; break;
        }
        message.Signature = SyncMessageSignature(state, flaw == SyncGossipFlaw.Signature ? 3 : (int)message.ValidatorIndex, message.Slot, HeadRoot);

        MessageValidity validity = await Deliver(node, GossipTopics.SyncCommitteeTopicNames[subnet], SyncCommitteeMessage.Encode(message));

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(validity, Is.EqualTo(expected));
        Assert.That(node.SyncPool.GetContribution(HeadSlot, HeadRoot, 0)?.AggregationBits, expected == MessageValidity.Accepted ? Is.EqualTo(Bits([2, 34, 66, 98])) : Is.Null);
    }

    public enum ContributionFlaw
    {
        None, AggregatorSeen, CoveredBits, FutureSlot, SubcommitteeIndex, NoParticipants, NotAggregator, AggregatorIndex, NotInSubcommittee,
        SelectionProof, AggregatorSignature, AggregateSignature, UnjoinedTopic,
    }

    // altair/p2p-interface.md sync_committee_contribution_and_proof: each case breaks one rule, and gets that rule's verdict.
    [TestCase(ContributionFlaw.None, MessageValidity.Accepted)]
    [TestCase(ContributionFlaw.AggregatorSeen, MessageValidity.Ignored)]
    [TestCase(ContributionFlaw.CoveredBits, MessageValidity.Ignored)]
    [TestCase(ContributionFlaw.FutureSlot, MessageValidity.Ignored)]
    [TestCase(ContributionFlaw.SubcommitteeIndex, MessageValidity.Rejected)]
    [TestCase(ContributionFlaw.NoParticipants, MessageValidity.Rejected)]
    [TestCase(ContributionFlaw.NotAggregator, MessageValidity.Rejected)]
    [TestCase(ContributionFlaw.AggregatorIndex, MessageValidity.Rejected)]
    [TestCase(ContributionFlaw.NotInSubcommittee, MessageValidity.Rejected)]
    [TestCase(ContributionFlaw.SelectionProof, MessageValidity.Rejected)]
    [TestCase(ContributionFlaw.AggregatorSignature, MessageValidity.Rejected)]
    [TestCase(ContributionFlaw.AggregateSignature, MessageValidity.Rejected)]
    [TestCase(ContributionFlaw.UnjoinedTopic, MessageValidity.Ignored)]
    public async Task Gossip_contribution_gets_the_verdict_of_the_rule_it_breaks_and_only_an_accepted_one_is_kept_as_the_best(ContributionFlaw flaw, MessageValidity expected)
    {
        using ValidatorNode node = new(Presets.FarFutureEpoch, HeadSlot, AttestationTimeMs);
        BeaconStateFulu state = SyncCommitteeHeadState(HeadSlot);
        node.Heads.Current = Head(state, HeadRoot, executionInSync: true);
        node.Gossip.SetSyncCommitteeSubnets(flaw == ContributionFlaw.UnjoinedTopic ? 0UL : 1UL);
        (int[] aggregators, int nonMemberAggregator, int nonAggregator) = SubcommitteeZeroSelection.Value;
        SyncCommitteeContribution contribution = Contribution(state, 0, 2);
        int aggregator = flaw switch { ContributionFlaw.NotAggregator => nonAggregator, ContributionFlaw.NotInSubcommittee => nonMemberAggregator, _ => aggregators[0] };
        switch (flaw)
        {
            case ContributionFlaw.AggregatorSeen: await DeliverContribution(Signed(state, aggregator, Contribution(state, 0, 3))); break;
            case ContributionFlaw.CoveredBits: await DeliverContribution(Signed(state, aggregators[1], Contribution(state, 0, 2, 3))); break;
            case ContributionFlaw.FutureSlot: contribution.Slot += 2; break;
            case ContributionFlaw.SubcommitteeIndex: contribution.SubcommitteeIndex = SyncCommitteeContribution.SubnetCount; break;
            case ContributionFlaw.NoParticipants: contribution.AggregationBits = new BitArray(SyncCommitteeContribution.SubcommitteeSize); break;
            case ContributionFlaw.AggregateSignature: contribution.Signature = GloasTestFixtures.AggregateSignature(SyncCommitteeSigningRoot(state, HeadSlot, HeadRoot), [2, 2, 2]); break;
        }
        SignedContributionAndProof signed = Signed(state, aggregator, contribution);
        switch (flaw)
        {
            case ContributionFlaw.AggregatorIndex: signed = Sign(state, new ContributionAndProof { AggregatorIndex = 1000, Contribution = contribution, SelectionProof = signed.Message!.SelectionProof }, aggregator); break;
            case ContributionFlaw.SelectionProof: signed = Sign(state, new ContributionAndProof
            {
                AggregatorIndex = (ulong)aggregator,
                Contribution = contribution,
                SelectionProof = SelectionProof(state, aggregators[1], HeadSlot, 0),
            }, aggregator); break;
            case ContributionFlaw.AggregatorSignature: signed = Sign(state, signed.Message!, aggregators[1]); break;
        }

        MessageValidity validity = await DeliverContribution(signed);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(validity, Is.EqualTo(expected));
        if (flaw is not (ContributionFlaw.AggregatorSeen or ContributionFlaw.CoveredBits))
            Assert.That(Encoded(node.SyncPool.GetBestContribution(HeadSlot, HeadRoot, 0)), Is.EqualTo(expected == MessageValidity.Accepted ? Encoded(contribution) : null));

        Task<MessageValidity> DeliverContribution(SignedContributionAndProof delivered) => Deliver(node, GossipTopics.SyncCommitteeContributionAndProof, SignedContributionAndProof.Encode(delivered));
    }

    [Test]
    public async Task Submitted_contributions_are_published_unless_gossip_already_has_them_and_the_best_of_each_vote_is_kept_for_two_slots()
    {
        using ValidatorNode node = new(Presets.FarFutureEpoch, HeadSlot, AttestationTimeMs);
        BeaconStateFulu state = SyncCommitteeHeadState(HeadSlot);
        node.Heads.Current = Head(state, HeadRoot, executionInSync: true);
        node.Gossip.SetSyncCommitteeSubnets(1);
        int[] aggregators = SubcommitteeZeroSelection.Value.Aggregators;
        SignedContributionAndProof accepted = Signed(state, aggregators[0], Contribution(state, 0, 2));
        SignedContributionAndProof sameAggregator = Signed(state, aggregators[0], Contribution(state, 0, 3));
        int nonAggregator = SubcommitteeZeroSelection.Value.NonAggregator;
        SignedContributionAndProof notSelected = Signed(state, nonAggregator, Contribution(state, 0, 3));

        IReadOnlyList<SubmissionFailure> failures = await node.Api.SubmitContributionAndProofs([accepted, sameAggregator, notSelected], CancellationToken.None);
        SyncCommitteeContribution? bestOfOne = node.SyncPool.GetBestContribution(HeadSlot, HeadRoot, 0);
        SyncCommitteeContribution better = Contribution(state, 0, 2, 3);
        MessageValidity betterOnGossip = await Deliver(node, GossipTopics.SyncCommitteeContributionAndProof, SignedContributionAndProof.Encode(Signed(state, aggregators[1], better)));
        SyncCommitteeContribution? bestOfTwo = node.SyncPool.GetBestContribution(HeadSlot, HeadRoot, 0);
        node.Clock.Add(TimeSpan.FromSeconds(2 * node.Spec.SecondsPerSlot));
        SyncCommitteeContribution? pruned = node.SyncPool.GetBestContribution(HeadSlot, HeadRoot, 0);

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(failures.Select(static failure => (failure.Index, failure.Message)), Is.EqualTo(new[] { (2, $"The selection proof does not select validator {nonAggregator} as an aggregator.") }),
            "a contribution the network has is no failure");
        Assert.That(node.Published, Is.EqualTo(new[]
        {
            (GossipTopics.Topic(ForkDigest.Compute(node.Spec, HeadEpoch), GossipTopics.SyncCommitteeContributionAndProof), Snappy.CompressToArray(SignedContributionAndProof.Encode(accepted))),
        }));
        Assert.That((Encoded(bestOfOne), betterOnGossip, Encoded(bestOfTwo), pruned), Is.EqualTo((Encoded(accepted.Message!.Contribution), MessageValidity.Accepted, Encoded(better), (SyncCommitteeContribution?)null)));
    }

    [Test]
    public async Task Sync_committee_subscriptions_join_and_advertise_their_subnets_and_the_contribution_topic_until_their_end()
    {
        using ValidatorNode node = new(Presets.FarFutureEpoch, HeadSlot, AttestationTimeMs);
        node.Store.EnsureSchemaVersion();
        Dictionary<string, GossipDigestWindowTests.RecordingTopic> topics = [];
        byte[] digest = ForkDigest.Compute(node.Spec, HeadEpoch);
        node.Gossip.Start(topicId => topics.TryGetValue(topicId, out GossipDigestWindowTests.RecordingTopic? topic) ? topic : topics[topicId] = new GossipDigestWindowTests.RecordingTopic(), digest);
        node.Discovery.CreateDiscv5Services(IPAddress.Loopback);
        ulong sequence = node.Metadata.Current.SeqNumber;
        ulong periodEnd = (SyncPeriod + 1) * Presets.EpochsPerSyncCommitteePeriod;
        // Positions 0 and 130 are in subcommittees 0 and 1, position 300 in subcommittee 2.
        SyncCommitteeSubscription soon = new(1, [0, 130], HeadEpoch + 1);
        SyncCommitteeSubscription wholePeriod = new(2, [300], periodEnd);

        BeaconNodeApiError? afterNextPeriod = await Refusal(() => node.Api.SubscribeToSyncCommittees(
            [soon, new SyncCommitteeSubscription(3, [1], periodEnd + Presets.EpochsPerSyncCommitteePeriod + 1)], CancellationToken.None).AsTask());
        BeaconNodeApiError? pastCommittee = await Refusal(() => node.Api.SubscribeToSyncCommittees([new SyncCommitteeSubscription(3, [Presets.SyncCommitteeSize], periodEnd)], CancellationToken.None).AsTask());
        ((ulong[], ulong[], ulong[], ulong) Subnets, bool Contribution) refused = SyncAdvertised();
        await node.Api.SubscribeToSyncCommittees([soon, wholePeriod], CancellationToken.None);
        ((ulong[], ulong[], ulong[], ulong), bool) subscribed = SyncAdvertised();
        node.SyncSubnets.Update(HeadEpoch + 1);
        ((ulong[], ulong[], ulong[], ulong), bool) afterSoon = SyncAdvertised();
        node.SyncSubnets.Update(periodEnd);
        ((ulong[], ulong[], ulong[], ulong), bool) afterPeriod = SyncAdvertised();

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That((afterNextPeriod, pastCommittee), Is.EqualTo(((BeaconNodeApiError?)BeaconNodeApiError.InvalidRequest, (BeaconNodeApiError?)BeaconNodeApiError.InvalidRequest)));
        Assert.That(refused, Is.EqualTo(((Array.Empty<ulong>(), Array.Empty<ulong>(), Array.Empty<ulong>(), sequence), false)), "a refused batch subscribes nothing");
        Assert.That(subscribed, Is.EqualTo(((new ulong[] { 0, 1, 2 }, new ulong[] { 0, 1, 2 }, new ulong[] { 0, 1, 2 }, sequence + 1), true)));
        Assert.That(afterSoon, Is.EqualTo(((new ulong[] { 2 }, new ulong[] { 2 }, new ulong[] { 2 }, sequence + 2), true)));
        Assert.That(afterPeriod, Is.EqualTo(((Array.Empty<ulong>(), Array.Empty<ulong>(), Array.Empty<ulong>(), sequence + 3), false)), "an empty bitfield leaves the ENR entry out");
        Assert.That(node.Heads.CaptureState, Is.True, "checking a joined subnet's messages needs the head state");

        ((ulong[], ulong[], ulong[], ulong), bool) SyncAdvertised() =>
            (Advertised(node, topics, digest, GossipTopics.SyncCommitteeTopicNames, node.Metadata.Current.Syncnets!, "syncnets"), IsJoined(topics, digest, GossipTopics.SyncCommitteeContributionAndProof));
    }

    private static byte[]? Encoded(SyncCommitteeContribution? contribution) => contribution is null ? null : SyncCommitteeContribution.Encode(contribution);

    private static BitArray Bits(int[] positions)
    {
        BitArray bits = new(SyncCommitteeContribution.SubcommitteeSize);
        foreach (int position in positions) bits[position] = true;
        return bits;
    }

    private static Task<MessageValidity> Deliver(ValidatorNode node, ulong subnet, SingleAttestation attestation) =>
        Deliver(node, GossipTopics.BeaconAttestationTopicNames[subnet], SingleAttestation.Encode(attestation));

    private static async Task<MessageValidity> Deliver(ValidatorNode node, string topicName, byte[] message)
    {
        TaskCompletionSource<MessageValidity> given = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GossipVerdict verdict = new(given.TrySetResult, null);
        MessageValidity checkedNow = node.Gossip.Handle(topicName, gloasTopic: false, Snappy.CompressToArray(message), verdict);
        if (!verdict.IsHandedOff) verdict.Complete(checkedNow);
        return await given.Task;
    }

    /// <summary>The subnets joined on <paramref name="digest"/>, set in <c>MetaData.attnets</c> and in the ENR <c>attnets</c> entry, and the metadata sequence number.</summary>
    private static (ulong[] Topics, ulong[] Metadata, ulong[] Enr, ulong Sequence) Advertised(ValidatorNode node, Dictionary<string, GossipDigestWindowTests.RecordingTopic> topics, byte[] digest) =>
        Advertised(node, topics, digest, GossipTopics.BeaconAttestationTopicNames, node.Metadata.Current.Attnets!, "attnets");

    /// <summary>The subnets of <paramref name="topicNames"/> joined on <paramref name="digest"/>, set in <paramref name="metadataBits"/> and in the ENR entry <paramref name="enrKey"/>, and the metadata sequence number.</summary>
    private static (ulong[] Topics, ulong[] Metadata, ulong[] Enr, ulong Sequence) Advertised(ValidatorNode node, Dictionary<string, GossipDigestWindowTests.RecordingTopic> topics, byte[] digest,
        string[] topicNames, BitArray metadataBits, string enrKey)
    {
        IEnumerable<ulong> subnets = Enumerable.Range(0, topicNames.Length).Select(static subnet => (ulong)subnet);
        BitArray? enr = node.Discovery.LocalNodeRecord.GetObj<byte[]>(enrKey) is { } bytes ? new BitArray(bytes) : null;
        return ([.. subnets.Where(subnet => IsJoined(topics, digest, topicNames[subnet]))],
            [.. subnets.Where(subnet => metadataBits[(int)subnet])],
            [.. subnets.Where(subnet => enr?[(int)subnet] == true)],
            node.Metadata.Current.SeqNumber);
    }

    private static bool IsJoined(Dictionary<string, GossipDigestWindowTests.RecordingTopic> topics, byte[] digest, string topicName) =>
        topics.TryGetValue(GossipTopics.Topic(digest, topicName), out GossipDigestWindowTests.RecordingTopic? topic) && topic.IsSubscribed;

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
        state.CurrentSyncCommittee = Committee(validators, static position => position % 3);
        state.NextSyncCommittee = Committee(validators, static position => 3 + position % 2);
        return state;
    }

    /// <summary>
    /// <see cref="HeadState"/> with a current sync committee whose first subcommittee holds validators 0 to 31 at four positions each, and
    /// whose others alternate validators 0 and 1, so validator 0 holds 4 positions of subcommittee 0 and 64 of each other, validator 2 only
    /// 4 of subcommittee 0, and validators 32 and up none.
    /// </summary>
    private static BeaconStateFulu SyncCommitteeHeadState(ulong slot)
    {
        BeaconStateFulu state = HeadState(slot);
        state.CurrentSyncCommittee = Committee(state.Validators!, static position => position < SyncCommitteeContribution.SubcommitteeSize ? position % 32 : position % 2);
        return state;
    }

    private static SyncCommittee Committee(Validator[] validators, Func<int, int> member) => new()
    {
        Pubkeys = [.. Enumerable.Range(0, Presets.SyncCommitteeSize).Select(position => validators[member(position)].Pubkey)],
        AggregatePubkey = GloasTestFixtures.Pubkey(0x60),
    };

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

    private static Hash256 SyncCommitteeSigningRoot(BeaconStateFulu state, ulong slot, Hash256 blockRoot) =>
        Domains.ComputeSigningRoot(blockRoot, state.GetDomain(DomainType.SyncCommittee, BeaconChainSpec.Mainnet.GetEpoch(slot)));

    private static SyncCommitteeMessage SyncMessage(BeaconStateFulu state, int validatorIndex, ulong slot, Hash256 blockRoot) => new()
    {
        Slot = slot,
        BeaconBlockRoot = blockRoot,
        ValidatorIndex = (ulong)validatorIndex,
        Signature = SyncMessageSignature(state, validatorIndex, slot, blockRoot),
    };

    private static BlsSignature SyncMessageSignature(BeaconStateFulu state, int signerIndex, ulong slot, Hash256 blockRoot) =>
        GloasTestFixtures.Sign(GloasTestFixtures.ValidatorKey(signerIndex), SyncCommitteeSigningRoot(state, slot, blockRoot));

    /// <summary>The contribution of subcommittee <paramref name="subcommitteeIndex"/> at <see cref="HeadSlot"/> for <see cref="HeadRoot"/>, aggregated from the messages of <paramref name="participants"/> at each of their positions.</summary>
    private static SyncCommitteeContribution Contribution(BeaconStateFulu state, ulong subcommitteeIndex, params int[] participants)
    {
        BlsPublicKey[] members = state.CurrentSyncCommittee!.Pubkeys!;
        int firstPosition = (int)subcommitteeIndex * SyncCommitteeContribution.SubcommitteeSize;
        BitArray bits = new(SyncCommitteeContribution.SubcommitteeSize);
        List<int> signers = [];
        for (int position = 0; position < bits.Length; position++)
        {
            int member = Array.IndexOf(participants, Array.FindIndex(state.Validators!, validator => validator.Pubkey == members[firstPosition + position]));
            if (member < 0) continue;
            bits[position] = true;
            signers.Add(participants[member]);
        }

        return new SyncCommitteeContribution
        {
            Slot = HeadSlot,
            BeaconBlockRoot = HeadRoot,
            SubcommitteeIndex = subcommitteeIndex,
            AggregationBits = bits,
            Signature = signers.Count == 0 ? default : GloasTestFixtures.AggregateSignature(SyncCommitteeSigningRoot(state, HeadSlot, HeadRoot), signers),
        };
    }

    private static BlsSignature SelectionProof(BeaconStateFulu state, int validatorIndex, ulong slot, ulong subcommitteeIndex) =>
        GloasTestFixtures.Sign(GloasTestFixtures.ValidatorKey(validatorIndex), Domains.ComputeSigningRoot(
            SszRoots.HashTreeRoot(new SyncAggregatorSelectionData { Slot = slot, SubcommitteeIndex = subcommitteeIndex }),
            state.GetDomain(DomainType.SyncCommitteeSelectionProof, BeaconChainSpec.Mainnet.GetEpoch(slot))));

    /// <summary><paramref name="contribution"/> with the selection proof of <paramref name="aggregatorIndex"/>, signed by it.</summary>
    private static SignedContributionAndProof Signed(BeaconStateFulu state, int aggregatorIndex, SyncCommitteeContribution contribution) => Sign(state, new ContributionAndProof
    {
        AggregatorIndex = (ulong)aggregatorIndex,
        Contribution = contribution,
        SelectionProof = SelectionProof(state, aggregatorIndex, contribution.Slot, contribution.SubcommitteeIndex),
    }, aggregatorIndex);

    private static SignedContributionAndProof Sign(BeaconStateFulu state, ContributionAndProof message, int signerIndex) => new()
    {
        Message = message,
        Signature = GloasTestFixtures.Sign(GloasTestFixtures.ValidatorKey(signerIndex), Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(message),
            state.GetDomain(DomainType.ContributionAndProof, BeaconChainSpec.Mainnet.GetEpoch(message.Contribution!.Slot)))),
    };

    /// <summary>
    /// Validators whose selection proof for subcommittee 0 at <see cref="HeadSlot"/> selects them, in and out of that subcommittee of
    /// <see cref="SyncCommitteeHeadState"/>, and a member whose proof does not.
    /// </summary>
    private static readonly Lazy<(int[] Aggregators, int NonMemberAggregator, int NonAggregator)> SubcommitteeZeroSelection = new(static () =>
    {
        BeaconStateFulu state = SyncCommitteeHeadState(HeadSlot);
        bool[] selected = [.. Enumerable.Range(0, ValidatorCount).Select(validator => BeaconStateAccessors.IsSyncCommitteeAggregator(SelectionProof(state, validator, HeadSlot, 0)))];
        return ([.. Enumerable.Range(0, 32).Where(validator => selected[validator])], Enumerable.Range(32, ValidatorCount - 32).First(validator => selected[validator]),
            Enumerable.Range(0, 32).First(validator => !selected[validator]));
    });

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
            Clock = new(DateTimeOffset.FromUnixTimeMilliseconds((long)(spec.GenesisTime + wallSlot * spec.SecondsPerSlot) * 1000 + millisecondsIntoSlot).UtcDateTime);
            _container = BeaconChainTestContainer.Builder()
                .AddSingleton(spec)
                .AddSingleton<ITimestamper>(Clock)
                .Build();
            Api = (BeaconNodeValidatorApi)_container.Resolve<IBeaconNodeValidatorApi>();
            Api.PublishGossip = (topic, message) => Published.Add((topic, message));
        }

        public BeaconNodeValidatorApi Api { get; }
        public List<(string Topic, byte[] Message)> Published { get; } = [];
        public BeaconChainSpec Spec => _container.Resolve<BeaconChainSpec>();
        public HeadSnapshotHolder Heads => _container.Resolve<HeadSnapshotHolder>();
        public BeaconSyncOrchestrator Orchestrator => _container.Resolve<BeaconSyncOrchestrator>();
        public ManualTimestamper Clock { get; }
        public GossipRouter Gossip => _container.Resolve<GossipRouter>();
        public AttestationPool Pool => _container.Resolve<AttestationPool>();
        public AttestationSubnets Subnets => _container.Resolve<AttestationSubnets>();
        public SyncCommitteePool SyncPool => _container.Resolve<SyncCommitteePool>();
        public SyncCommitteeSubnets SyncSubnets => _container.Resolve<SyncCommitteeSubnets>();
        public LocalMetadataSource Metadata => _container.Resolve<LocalMetadataSource>();
        public BeaconDiscovery Discovery => _container.Resolve<BeaconDiscovery>();
        public BeaconChainStore Store => _container.Resolve<BeaconChainStore>();
        public FailedBlockRoots FailedBlocks => _container.Resolve<FailedBlockRoots>();

        public void Dispose() => _container.Dispose();
    }
}
