// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.StateTransition.Shuffling;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.StateTransition;

[HardTimeout(60_000)]
public class GloasOperationsTests
{
    private static readonly ulong SlotsPerEpoch = Presets.SlotsPerEpoch;


    [Test]
    public void ProcessProposerSlashing_slashes_the_proposer_and_clears_the_pending_builder_payment_for_its_proposal()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        PubkeyCache pubkeys = InstallRealValidatorKeys(state);
        EpochCache cache = new();
        const ulong bidValue = 5 * Gwei;

        // Slot 32: the bid's payment is recorded for that slot's proposer at upper-half index 32.
        int proposer = (int)state.GetBeaconProposerIndex();
        ApplyBlock(state, MinimalBlock(state, ValidBuilderBid(state, builderSk, builderIndex: 0, value: bidValue)), cache);
        GloasSlotProcessing.ProcessSlots(state, 33, cache);
        Assert.That(state.BuilderPendingPayments![32].ProposerIndex, Is.EqualTo((ulong)proposer), "fixture bug: the payment must name the slot-32 proposer");

        // Give slot 33 a different proposer so the whistleblower reward and the penalty land on different validators.
        const int whistleblower = 7;
        state.ProposerLookahead![33 % 32] = whistleblower;
        ulong proposerBalanceBefore = state.Balances![proposer];
        ulong whistleblowerBalanceBefore = state.Balances[whistleblower];

        GloasBlockProcessing.ProcessProposerSlashing(state, Equivocation(state, slot: 32, proposer), cache, pubkeys, verifySignatures: true);

        Validator slashed = state.Validators![proposer];
        const ulong effectiveBalance = 32 * Gwei;
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(slashed.Slashed, Is.True);
        Assert.That(slashed.ExitEpoch, Is.Not.EqualTo(Presets.FarFutureEpoch), "slashing must initiate the exit");
        Assert.That(slashed.WithdrawableEpoch, Is.EqualTo(state.GetCurrentEpoch() + Presets.EpochsPerSlashingsVector));
        Assert.That(state.Slashings![(int)state.GetCurrentEpoch()], Is.EqualTo(effectiveBalance));
        Assert.That(state.Balances[proposer], Is.EqualTo(proposerBalanceBefore - effectiveBalance / Presets.MinSlashingPenaltyQuotientElectra));
        Assert.That(state.Balances[whistleblower], Is.EqualTo(whistleblowerBalanceBefore + effectiveBalance / Presets.WhistleblowerRewardQuotientElectra));
        Assert.That(state.BuilderPendingPayments[32].Withdrawal!.Amount, Is.EqualTo(0ul), "the equivocating proposer's payment must be cleared");
        Assert.That(state.BuilderPendingPayments[32].ProposerIndex, Is.EqualTo(0ul));
    }

    [Test]
    public void ProcessProposerSlashing_clears_a_previous_epoch_payment_through_the_rotated_lower_half()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        PubkeyCache pubkeys = InstallRealValidatorKeys(state);
        EpochCache cache = new();

        int proposer = (int)state.GetBeaconProposerIndex();
        ApplyBlock(state, MinimalBlock(state, ValidBuilderBid(state, builderSk, builderIndex: 0, value: 5 * Gwei)), cache);
        // Crossing into epoch 2 moves the unsettled slot-32 payment from index 32 to index 0.
        GloasSlotProcessing.ProcessSlots(state, 2 * SlotsPerEpoch, cache);
        Assert.That(state.BuilderPendingPayments![0].Withdrawal!.Amount, Is.EqualTo(5 * Gwei), "fixture bug: the payment must have rotated into the previous-epoch half");

        GloasBlockProcessing.ProcessProposerSlashing(state, Equivocation(state, slot: 32, proposer), cache, pubkeys, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(state.Validators![proposer].Slashed, Is.True);
        Assert.That(state.BuilderPendingPayments[0].Withdrawal!.Amount, Is.EqualTo(0ul), "the previous-epoch payment must be cleared through its rotated address");
    }

    [Test]
    public void ProcessProposerSlashing_leaves_a_payment_recorded_for_another_proposer_alone()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        PubkeyCache pubkeys = InstallRealValidatorKeys(state);
        EpochCache cache = new();
        const ulong bidValue = 5 * Gwei;

        int proposer = (int)state.GetBeaconProposerIndex();
        ApplyBlock(state, MinimalBlock(state, ValidBuilderBid(state, builderSk, builderIndex: 0, value: bidValue)), cache);
        GloasSlotProcessing.ProcessSlots(state, 33, cache);

        // Headers for the same slot from a validator that was not its proposer: slashable, but not this payment's owner.
        const int bystander = 5;
        Assert.That(proposer, Is.Not.EqualTo(bystander), "fixture bug");
        GloasBlockProcessing.ProcessProposerSlashing(state, Equivocation(state, slot: 32, bystander), cache, pubkeys, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(state.Validators![bystander].Slashed, Is.True);
        Assert.That(state.BuilderPendingPayments![32].Withdrawal!.Amount, Is.EqualTo(bidValue), "an unrelated equivocation must not grief the honest proposer's payment");
        Assert.That(state.BuilderPendingPayments[32].ProposerIndex, Is.EqualTo((ulong)proposer));
    }

    [TestCase("identical headers", "identical")]
    [TestCase("different slots", "header slots do not match")]
    [TestCase("different proposers", "proposer indices do not match")]
    [TestCase("index out of range", "is out of range")]
    [TestCase("bad signature", "Invalid proposer slashing signature")]
    [TestCase("already slashed", "not slashable")]
    public void ProcessProposerSlashing_rejects_an_invalid_slashing_and_mutates_nothing(string defect, string expectedMessage)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        PubkeyCache pubkeys = InstallRealValidatorKeys(state);
        const int proposer = 3;
        ProposerSlashing slashing = Equivocation(state, slot: 32, proposer);
        switch (defect)
        {
            case "identical headers":
                slashing.SignedHeader2 = slashing.SignedHeader1;
                break;
            case "different slots":
                // Two honest proposals at consecutive slots, each validly signed: not an equivocation.
                slashing.SignedHeader2 = SignedHeader(state, slot: 33, proposer, Hash(0x22));
                break;
            case "different proposers":
                // Each header is validly signed by the validator it names; only the index check rejects the pair.
                slashing.SignedHeader2 = SignedHeader(state, slot: 32, proposer + 1, Hash(0x22));
                break;
            case "index out of range":
                slashing = Equivocation(state, slot: 32, ValidatorCount);
                break;
            case "bad signature":
                slashing.SignedHeader2!.Signature = Corrupt(slashing.SignedHeader2.Signature);
                break;
            case "already slashed":
                Validator alreadySlashed = state.Validators![proposer].Clone();
                alreadySlashed.Slashed = true;
                state.Validators[proposer] = alreadySlashed;
                break;
        }
        AssertRefusedWithoutMutation(state, () =>
            GloasBlockProcessing.ProcessProposerSlashing(state, slashing, new EpochCache(), pubkeys, verifySignatures: true), expectedMessage, "a rejected slashing must leave the state untouched");
    }


    [Test]
    public void ProcessAttesterSlashing_slashes_exactly_the_validators_in_both_conflicting_votes()
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        PubkeyCache pubkeys = InstallRealValidatorKeys(state);
        // A double vote: same target epoch, different data.
        AttesterSlashingGloas slashing = new()
        {
            Attestation1 = SignedIndexedAttestation(state, Vote(slot: 32, sourceEpoch: 0, targetEpoch: 1, fill: 0xA0), [1, 2, 3]),
            Attestation2 = SignedIndexedAttestation(state, Vote(slot: 32, sourceEpoch: 0, targetEpoch: 1, fill: 0xB0), [2, 3, 4]),
        };
        ulong balanceBefore = state.Balances![2];

        GloasBlockProcessing.ProcessAttesterSlashing(state, slashing, new EpochCache(), pubkeys, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(state.Validators!.Select(v => v.Slashed).Take(6), Is.EqualTo(new[] { false, false, true, true, false, false }).AsCollection);
        Assert.That(state.Balances[2], Is.EqualTo(balanceBefore - 32 * Gwei / Presets.MinSlashingPenaltyQuotientElectra));
        Assert.That(state.Slashings![1], Is.EqualTo(2 * 32 * Gwei));
    }

    [Test]
    public void ProcessAttesterSlashing_skips_a_validator_in_both_votes_that_was_already_slashed()
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        PubkeyCache pubkeys = InstallRealValidatorKeys(state);
        EpochCache cache = new();
        const int alreadySlashed = 2;
        const int stillSlashable = 3;
        state.SlashValidator(alreadySlashed, cache);
        ulong slashedBalanceBefore = state.Balances![alreadySlashed];
        ulong slashingsBefore = state.Slashings![1];
        Assert.That(slashingsBefore, Is.EqualTo(32 * Gwei), "fixture bug: the first slashing must have been accounted");
        AttesterSlashingGloas slashing = new()
        {
            Attestation1 = SignedIndexedAttestation(state, Vote(slot: 32, sourceEpoch: 0, targetEpoch: 1, fill: 0xA0), [1, alreadySlashed, stillSlashable]),
            Attestation2 = SignedIndexedAttestation(state, Vote(slot: 32, sourceEpoch: 0, targetEpoch: 1, fill: 0xB0), [alreadySlashed, stillSlashable, 4]),
        };

        GloasBlockProcessing.ProcessAttesterSlashing(state, slashing, cache, pubkeys, verifySignatures: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(state.Validators![stillSlashable].Slashed, Is.True);
        Assert.That(state.Balances[alreadySlashed], Is.EqualTo(slashedBalanceBefore), "a validator slashed once must not be penalized again");
        Assert.That(state.Slashings[1], Is.EqualTo(slashingsBefore + 32 * Gwei), "only the newly slashed validator's balance joins the slashings accounting");
    }

    [TestCase("no intersection", "slashed no validator")]
    [TestCase("every common validator already slashed", "slashed no validator")]
    [TestCase("same vote twice", "not slashable")]
    [TestCase("unsorted indices", "attestation 1 is invalid")]
    [TestCase("bad signature", "attestation 2 is invalid")]
    public void ProcessAttesterSlashing_rejects_an_invalid_slashing_and_mutates_nothing(string defect, string expectedMessage)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        PubkeyCache pubkeys = InstallRealValidatorKeys(state);
        AttestationData vote1 = Vote(slot: 32, sourceEpoch: 0, targetEpoch: 1, fill: 0xA0);
        AttestationData vote2 = Vote(slot: 32, sourceEpoch: 0, targetEpoch: 1, fill: 0xB0);
        AttesterSlashingGloas slashing = defect switch
        {
            "no intersection" => new() { Attestation1 = SignedIndexedAttestation(state, vote1, [1, 2]), Attestation2 = SignedIndexedAttestation(state, vote2, [3, 4]) },
            "every common validator already slashed" => new() { Attestation1 = SignedIndexedAttestation(state, vote1, [1, 2]), Attestation2 = SignedIndexedAttestation(state, vote2, [2, 3]) },
            "same vote twice" => new() { Attestation1 = SignedIndexedAttestation(state, vote1, [1, 2]), Attestation2 = SignedIndexedAttestation(state, vote1, [2, 3]) },
            "unsorted indices" => new() { Attestation1 = SignedIndexedAttestation(state, vote1, [2, 1]), Attestation2 = SignedIndexedAttestation(state, vote2, [2, 3]) },
            "bad signature" => new() { Attestation1 = SignedIndexedAttestation(state, vote1, [1, 2]), Attestation2 = SignedIndexedAttestation(state, vote2, [2, 3]) },
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        if (defect == "bad signature")
            slashing.Attestation2!.Signature = Corrupt(slashing.Attestation2.Signature);
        if (defect == "every common validator already slashed")
            state.SlashValidator(2, new EpochCache());
        AssertRefusedWithoutMutation(state, () =>
            GloasBlockProcessing.ProcessAttesterSlashing(state, slashing, new EpochCache(), pubkeys, verifySignatures: true), expectedMessage, "a rejected slashing must leave the state untouched");
    }

    [Test]
    public void IsValidIndexedAttestation_enforces_the_eip7688_bound_that_replaced_the_lists_ssz_limit()
    {
        const int bound = Presets.MaxValidatorsPerCommittee * Presets.MaxCommitteesPerSlot;
        // Enough validators that a strictly ascending, in-range index list can exceed the bound.
        BeaconStateGloas state = new() { Validators = [.. Enumerable.Repeat(new Validator(), bound + 1)] };
        static IndexedAttestationGloas WithIndices(int count) => new()
        {
            AttestingIndices = [.. Enumerable.Range(0, count).Select(i => (ulong)i)],
            Data = Vote(slot: 0, sourceEpoch: 0, targetEpoch: 0, fill: 0xA0),
        };

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(GloasBlockProcessing.IsValidIndexedAttestation(state, WithIndices(bound), new PubkeyCache(), verifySignature: false), Is.True);
        Assert.That(GloasBlockProcessing.IsValidIndexedAttestation(state, WithIndices(bound + 1), new PubkeyCache(), verifySignature: false), Is.False);
    }


    [Test]
    public void ProcessAttestation_sets_the_same_flags_and_proposer_reward_as_the_fulu_pipeline_for_a_same_slot_vote()
    {
        BeaconStateFulu pre = CreateFuluStateAtBoundary(ValidatorCount);
        BeaconStateFulu fulu = pre.Clone();
        BeaconStateGloas gloas = GloasForkTransition.UpgradeToGloas(pre, SyntheticSpec());
        // Both twins: a block landed at slot 32 (its root breaks the skipped-slot run), the state
        // is at slot 33, and validator 11 proposes there.
        const int proposer = 11;
        Hash256 blockRoot = Hash(0xB1);
        foreach ((ulong[] lookahead, Hash256[] blockRoots) in new[] { (fulu.ProposerLookahead!, fulu.BlockRoots!), (gloas.ProposerLookahead!, gloas.BlockRoots!) })
        {
            lookahead[1] = proposer;
            blockRoots[32] = blockRoot;
        }
        fulu.Slot = 33;
        gloas.Slot = 33;
        EpochCache cache = new();
        CommitteeCache committees = cache.GetCommitteeCache(gloas, 1);
        AttestationGloas attestation = CommitteeAttestation(gloas, VoteFor(gloas, slot: 32, targetEpoch: 1, blockRoot), committees, committeeIndex: 0, sign: false);
        Assert.That(gloas.IsAttestationSameSlot(attestation.Data!), Is.True, "fixture bug: the vote must be for the block proposed at its slot");

        GloasBlockProcessing.ProcessAttestation(gloas, attestation, parentSlot: 32, cache, new PubkeyCache(), verifySignature: false);
        BlockProcessing.ProcessAttestation(fulu, ToFuluAttestation(attestation), new EpochCache(), new PubkeyCache(), verifySignature: false);

        ulong[] attesters = gloas.GetAttestingIndices(attestation, committees);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(attesters, Has.Length.GreaterThan(1), "fixture bug");
        Assert.That(attesters.Select(i => gloas.CurrentEpochParticipation![i]), Has.All.EqualTo(0b111), "a timely same-slot vote earns all three flags");
        Assert.That(gloas.CurrentEpochParticipation, Is.EqualTo(fulu.CurrentEpochParticipation).AsCollection);
        Assert.That(gloas.Balances![proposer], Is.GreaterThan(32 * Gwei), "the proposer must actually have been rewarded");
        Assert.That(gloas.Balances, Is.EqualTo(fulu.Balances).AsCollection);
    }

    [TestCase(0UL, true, 0b011)]
    [TestCase(1UL, true, 0b111)]
    [TestCase(0UL, false, 0b111)]
    [TestCase(1UL, false, 0b011)]
    public void ProcessAttestation_grants_the_head_flag_only_when_the_index_matches_the_parents_payload_availability(ulong index, bool payloadAvailable, int expectedFlags)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        EpochCache cache = new();
        // Slot 32 is skipped, so its root repeats slot 31's: a vote for it is not a same-slot vote
        // and the payload status it claims is checked against the parent block's slot instead.
        GloasSlotProcessing.ProcessSlots(state, 33, cache);
        const ulong parentSlot = 0;
        Assert.That(state.LatestBlockHeader!.Slot, Is.EqualTo(parentSlot), "fixture bug");
        state.ExecutionPayloadAvailability![(int)parentSlot] = payloadAvailable;
        AttestationData data = VoteFor(state, slot: 32, targetEpoch: 1, state.GetBlockRootAtSlot(32), index);
        Assert.That(state.IsAttestationSameSlot(data), Is.False, "fixture bug");
        CommitteeCache committees = cache.GetCommitteeCache(state, 1);
        AttestationGloas attestation = CommitteeAttestation(state, data, committees, committeeIndex: 0, sign: false);

        GloasBlockProcessing.ProcessAttestation(state, attestation, parentSlot, cache, new PubkeyCache(), verifySignature: false);

        ulong[] attesters = state.GetAttestingIndices(attestation, committees);
        Assert.That(attesters.Select(i => state.CurrentEpochParticipation![i]), Has.All.EqualTo(expectedFlags));
    }

    [TestCase(1UL, "must carry index 0")]
    [TestCase(2UL, "must encode a payload status")]
    public void ProcessAttestation_rejects_a_same_slot_vote_whose_index_is_not_zero(ulong index, string expectedMessage)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        EpochCache cache = new();
        ApplyBlock(state, MinimalBlock(state, SelfBuildBid(state, state.LatestBlockHash!, Hash(0x99))), cache);
        GloasSlotProcessing.ProcessSlots(state, 33, cache);
        AttestationData data = VoteFor(state, slot: 32, targetEpoch: 1, state.GetBlockRootAtSlot(32), index);
        Assert.That(state.IsAttestationSameSlot(data), Is.True, "fixture bug");
        AttestationGloas attestation = CommitteeAttestation(state, data, cache.GetCommitteeCache(state, 1), committeeIndex: 0, sign: false);

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            GloasBlockProcessing.ProcessAttestation(state, attestation, parentSlot: 32, cache, new PubkeyCache(), verifySignature: false))!;

        Assert.That(ex.Message, Does.Contain(expectedMessage));
        Assert.That(state.CurrentEpochParticipation, Has.All.EqualTo(0));
    }

    [TestCase("target two epochs back", "not the previous or current epoch")]
    [TestCase("target epoch not the slot's", "does not match its slot")]
    [TestCase("included in its own slot", "included too early")]
    [TestCase("source not the justified checkpoint", "does not match the justified checkpoint")]
    public void ProcessAttestation_rejects_a_vote_whose_data_fails_a_structural_check_and_mutates_nothing(string defect, string expectedMessage)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        EpochCache cache = new();
        // Epoch 2 for the two-epochs-back case (epoch 0 is then out of range); slot 33 otherwise.
        GloasSlotProcessing.ProcessSlots(state, defect == "target two epochs back" ? 2 * SlotsPerEpoch + 1 : 33, cache);
        AttestationData data = defect switch
        {
            "target two epochs back" => VoteFor(state, slot: 8, targetEpoch: 0, state.GetBlockRootAtSlot(8)),
            "target epoch not the slot's" => VoteFor(state, slot: 31, targetEpoch: 1, state.GetBlockRootAtSlot(31)),
            "included in its own slot" => VoteFor(state, slot: 33, targetEpoch: 1, Hash(0x33)),
            "source not the justified checkpoint" => VoteFor(state, slot: 32, targetEpoch: 1, state.GetBlockRootAtSlot(32)),
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        if (defect == "source not the justified checkpoint")
            data.Source = new Checkpoint { Epoch = data.Source!.Epoch, Root = Hash(0x55) };
        CommitteeCache committees = cache.GetCommitteeCache(state, BeaconStateAccessors.ComputeEpochAtSlot(data.Slot));
        AttestationGloas attestation = CommitteeAttestation(state, data, committees, committeeIndex: 0, sign: false);
        AssertRefusedWithoutMutation(state, () =>
            GloasBlockProcessing.ProcessAttestation(state, attestation, parentSlot: state.LatestBlockHeader!.Slot, cache, new PubkeyCache(), verifySignature: false), expectedMessage, "a rejected attestation must leave the state untouched");
    }

    [Test]
    public void ProcessAttestation_adds_each_first_time_participants_effective_balance_to_the_same_slot_builder_payment_weight()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        PubkeyCache pubkeys = InstallRealValidatorKeys(state);
        EpochCache cache = new();
        const int paymentIndex = 32;
        ApplyBlock(state, MinimalBlock(state, ValidBuilderBid(state, builderSk, builderIndex: 0, value: 5 * Gwei)), cache);
        GloasSlotProcessing.ProcessSlots(state, 33, cache);
        Assert.That(state.BuilderPendingPayments![paymentIndex].Weight, Is.EqualTo(0ul), "fixture bug");

        CommitteeCache committees = cache.GetCommitteeCache(state, 1);
        AttestationData data = VoteFor(state, slot: 32, targetEpoch: 1, state.GetBlockRootAtSlot(32));
        Assert.That(state.IsAttestationSameSlot(data), Is.True, "fixture bug");
        AttestationGloas attestation = CommitteeAttestation(state, data, committees, committeeIndex: 0, sign: true);
        ulong[] attesters = state.GetAttestingIndices(attestation, committees);
        ulong expectedWeight = attesters.Aggregate(0ul, (sum, i) => sum + state.Validators![i].EffectiveBalance);

        GloasBlockProcessing.ProcessAttestation(state, attestation, parentSlot: 32, cache, pubkeys, verifySignature: true);

        Assert.Multiple(() =>
        {
            Assert.That(state.BuilderPendingPayments[paymentIndex].Weight, Is.EqualTo(expectedWeight));
            Assert.That(state.BuilderPendingPayments[paymentIndex].Withdrawal!.Amount, Is.EqualTo(5 * Gwei), "weighing the payment must not disturb the payment itself");
            Assert.That(attesters.Select(i => state.CurrentEpochParticipation![i]), Has.All.EqualTo(0b111));
        });

        // The same participants again set no new flag, so they weigh nothing more.
        GloasBlockProcessing.ProcessAttestation(state, attestation, parentSlot: 32, cache, pubkeys, verifySignature: true);
        Assert.That(state.BuilderPendingPayments[paymentIndex].Weight, Is.EqualTo(expectedWeight));
    }

    [Test]
    public void ProcessAttestation_weighs_the_payment_only_for_a_same_slot_vote_from_a_validator_with_no_prior_participation()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        EpochCache cache = new();
        const int paymentIndex = 32;
        ApplyBlock(state, MinimalBlock(state, ValidBuilderBid(state, builderSk, builderIndex: 0, value: 5 * Gwei)), cache);
        GloasSlotProcessing.ProcessSlots(state, 33, cache);
        CommitteeCache committees = cache.GetCommitteeCache(state, 1);

        // A wrong-head vote: new source and target flags, but not a same-slot vote.
        AttestationGloas wrongHead = CommitteeAttestation(state, VoteFor(state, slot: 32, targetEpoch: 1, Hash(0x33)), committees, committeeIndex: 0, sign: false);
        GloasBlockProcessing.ProcessAttestation(state, wrongHead, parentSlot: 32, cache, new PubkeyCache(), verifySignature: false);
        ulong[] attesters = state.GetAttestingIndices(wrongHead, committees);
        Assert.That(attesters.Select(i => state.CurrentEpochParticipation![i]), Has.All.EqualTo(0b011), "fixture bug: the flags must have been newly set");
        Assert.That(state.BuilderPendingPayments![paymentIndex].Weight, Is.EqualTo(0ul), "a vote that is not for the slot's own block weighs nothing");

        // The correct same-slot vote from the same validators sets a new (head) flag, but they had participated already.
        AttestationGloas sameSlot = CommitteeAttestation(state, VoteFor(state, slot: 32, targetEpoch: 1, state.GetBlockRootAtSlot(32)), committees, committeeIndex: 0, sign: false);
        Assert.That(state.IsAttestationSameSlot(sameSlot.Data!), Is.True, "fixture bug");
        GloasBlockProcessing.ProcessAttestation(state, sameSlot, parentSlot: 32, cache, new PubkeyCache(), verifySignature: false);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(attesters.Select(i => state.CurrentEpochParticipation![i]), Has.All.EqualTo(0b111), "fixture bug: the head flag must have been newly set");
        Assert.That(state.BuilderPendingPayments[paymentIndex].Weight, Is.EqualTo(0ul), "only a validator's first participation in the epoch weighs the payment");
    }

    [Test]
    public void ProcessAttestation_for_a_previous_epoch_target_records_participation_and_weight_in_the_previous_epoch_halves()
    {
        BeaconStateGloas state = CreateGloasState(out Bls.SecretKey builderSk, out _);
        PubkeyCache pubkeys = InstallRealValidatorKeys(state);
        EpochCache cache = new();
        ApplyBlock(state, MinimalBlock(state, ValidBuilderBid(state, builderSk, builderIndex: 0, value: 5 * Gwei)), cache);
        // Epoch 2: the slot-32 payment has rotated to index 0 and epoch 1's participation is now "previous".
        GloasSlotProcessing.ProcessSlots(state, 2 * SlotsPerEpoch, cache);
        Assert.That(state.BuilderPendingPayments![0].Withdrawal!.Amount, Is.EqualTo(5 * Gwei), "fixture bug");

        CommitteeCache committees = cache.GetCommitteeCache(state, 1);
        AttestationData data = VoteFor(state, slot: 32, targetEpoch: 1, state.GetBlockRootAtSlot(32));
        AttestationGloas attestation = CommitteeAttestation(state, data, committees, committeeIndex: 0, sign: true);
        ulong[] attesters = state.GetAttestingIndices(attestation, committees);
        ulong expectedWeight = attesters.Aggregate(0ul, (sum, i) => sum + state.Validators![i].EffectiveBalance);

        GloasBlockProcessing.ProcessAttestation(state, attestation, parentSlot: 32, cache, pubkeys, verifySignature: true);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        // Included 32 slots late: the target flag alone is still earned.
        Assert.That(attesters.Select(i => state.PreviousEpochParticipation![i]), Has.All.EqualTo(0b010));
        Assert.That(state.CurrentEpochParticipation, Has.All.EqualTo(0));
        Assert.That(state.BuilderPendingPayments[0].Weight, Is.EqualTo(expectedWeight));
        Assert.That(state.BuilderPendingPayments[32].Weight, Is.EqualTo(0ul));
    }

    [Test]
    public void ProcessAttestation_rejects_a_bad_aggregate_signature()
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        PubkeyCache pubkeys = InstallRealValidatorKeys(state);
        EpochCache cache = new();
        GloasSlotProcessing.ProcessSlots(state, 33, cache);
        AttestationGloas attestation = CommitteeAttestation(state, VoteFor(state, slot: 32, targetEpoch: 1, state.GetBlockRootAtSlot(32)), cache.GetCommitteeCache(state, 1), committeeIndex: 0, sign: true);
        attestation.Signature = Corrupt(attestation.Signature);

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            GloasBlockProcessing.ProcessAttestation(state, attestation, parentSlot: 0, cache, pubkeys, verifySignature: true))!;

        Assert.That(ex.Message, Does.Contain("Invalid indexed attestation"));
        Assert.That(state.CurrentEpochParticipation, Has.All.EqualTo(0));
    }


    private static BeaconStateGloas StateProcessingBlock33(out PubkeyCache pubkeys, out Hash256 parentRoot)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        pubkeys = InstallRealValidatorKeys(state);
        EpochCache cache = new();
        ApplyBlock(state, MinimalBlock(state, SelfBuildBid(state, state.LatestBlockHash!, Hash(0x99))), cache);
        GloasSlotProcessing.ProcessSlots(state, 33, cache);
        GloasBlockProcessing.ProcessBlockHeader(state, MinimalBlock(state, SelfBuildBid(state, state.LatestBlockHash!, Hash(0x9A))).Message!);
        parentRoot = state.LatestBlockHeader!.ParentRoot!;
        Assert.That(parentRoot, Is.EqualTo(state.GetBlockRootAtSlot(32)), "fixture bug: the parent must be the slot-32 block");
        return state;
    }

    [Test]
    public void ProcessPayloadAttestation_accepts_a_ptc_vote_for_the_parent_block_signed_by_the_set_members()
    {
        BeaconStateGloas state = StateProcessingBlock33(out PubkeyCache pubkeys, out Hash256 parentRoot);
        int[] positions = [0, 1, 2, 5, 9];
        PayloadAttestationData data = new() { BeaconBlockRoot = parentRoot, Slot = 32, PayloadPresent = true, BlobDataAvailable = true };
        PayloadAttestation attestation = PtcAttestation(state, data, positions, sign: true);

        Assert.DoesNotThrow(() => GloasBlockProcessing.ProcessPayloadAttestation(state, attestation, UpgradeEpochSpec(), pubkeys, verifySignature: true));

        ulong[] ptc = state.GetPtc(32, UpgradeEpochSpec()).Indices!;
        IndexedPayloadAttestation indexed = state.GetIndexedPayloadAttestation(attestation, UpgradeEpochSpec());
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(indexed.AttestingIndices, Is.EqualTo(positions.Select(p => ptc[p]).Order()).AsCollection);
        Assert.That(indexed.AttestingIndices!.Distinct().Count(), Is.GreaterThan(1), "fixture bug: the vote must carry several real committee members");
    }

    [TestCase("wrong block root", "not for the parent beacon block")]
    [TestCase("wrong slot", "not for the slot before")]
    [TestCase("bad signature", "Invalid indexed payload attestation")]
    [TestCase("no bits set", "Invalid indexed payload attestation")]
    public void ProcessPayloadAttestation_rejects_an_invalid_vote(string defect, string expectedMessage)
    {
        BeaconStateGloas state = StateProcessingBlock33(out PubkeyCache pubkeys, out Hash256 parentRoot);
        PayloadAttestationData data = new()
        {
            BeaconBlockRoot = defect == "wrong block root" ? Hash(0x44) : parentRoot,
            Slot = defect == "wrong slot" ? 31UL : 32UL,
            PayloadPresent = true,
            BlobDataAvailable = true,
        };
        // A pre-fork slot has no populated committee to sign as; its rejection comes before the signature.
        PayloadAttestation attestation = PtcAttestation(state, data, defect == "no bits set" ? [] : [0, 1, 2], sign: defect is not ("no bits set" or "wrong slot"));
        if (defect == "bad signature")
            attestation.Signature = Corrupt(attestation.Signature);

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            GloasBlockProcessing.ProcessPayloadAttestation(state, attestation, UpgradeEpochSpec(), pubkeys, verifySignature: true))!;

        Assert.That(ex.Message, Does.Contain(expectedMessage));
    }

    // PTC sampling repeats validators: indexed payload attestations require sorted indices, not uniqueness.
    [TestCase(new ulong[] { 4, 5 }, true)]
    [TestCase(new ulong[] { 5, 5 }, true)]
    [TestCase(new ulong[] { 5, 4 }, false)]
    [TestCase(new ulong[0], false)]
    [TestCase(new ulong[] { ValidatorCount }, false)]
    public void IsValidIndexedPayloadAttestation_requires_sorted_in_range_indices_but_allows_a_member_holding_several_seats(ulong[] indices, bool expected)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        PubkeyCache pubkeys = InstallRealValidatorKeys(state);
        PayloadAttestationData data = new() { BeaconBlockRoot = Hash(0x31), Slot = 32, PayloadPresent = true, BlobDataAvailable = true };
        Hash256 domain = state.GetDomain(DomainType.PtcAttester, BeaconStateAccessors.ComputeEpochAtSlot(data.Slot));
        Hash256 signingRoot = Domains.ComputeSigningRoot(SszRoots.HashTreeRoot(data), domain);
        IndexedPayloadAttestation attestation = new()
        {
            AttestingIndices = indices,
            Data = data,
            // Signed once per listed seat, which is what a repeated member's aggregate carries.
            Signature = indices.Length == 0 ? default : AggregateSignature(signingRoot, [.. indices.Select(i => (int)i)]),
        };

        Assert.That(GloasBlockProcessing.IsValidIndexedPayloadAttestation(state, attestation, pubkeys, verifySignature: true), Is.EqualTo(expected));
    }

    [TestCase(0, false)]
    [TestCase(1, true)]
    public void IsValidIndexedPayloadAttestation_refuses_empty_indices_without_relying_on_the_signature(int indexCount, bool expected)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        PubkeyCache pubkeys = InstallRealValidatorKeys(state);
        IndexedPayloadAttestation attestation = new()
        {
            AttestingIndices = [.. Enumerable.Range(4, indexCount).Select(static i => (ulong)i)],
            Data = new PayloadAttestationData { BeaconBlockRoot = Hash(0x31), Slot = 32, PayloadPresent = true, BlobDataAvailable = true },
        };

        Assert.That(GloasBlockProcessing.IsValidIndexedPayloadAttestation(state, attestation, pubkeys, verifySignature: false), Is.EqualTo(expected));
    }

    // get_ptc must reject pre-fork epochs even though placeholder committees can name validator 0.
    [TestCase(true, TestName = "ProcessBlock_refuses_a_first_gloas_block_whose_payload_attestation_names_the_last_fulu_slot")]
    [TestCase(false, TestName = "ProcessBlock_accepts_a_payload_attestation_naming_the_first_gloas_slot")]
    public void ProcessBlock_accepts_payload_attestations_only_for_gloas_slots(bool namesLastFuluSlot)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        // What a decoded upgrade state holds: initialize_ptc_window fills the pre-fork half with validator 0.
        for (int i = 0; i < (int)SlotsPerEpoch; i++)
            state.PtcWindow![i] = new PayloadTimelinessCommittee { Indices = new ulong[Presets.PtcSize] };
        EpochCache cache = new();
        if (!namesLastFuluSlot)
        {
            ApplyBlock(state, MinimalBlock(state, SelfBuildBid(state, state.LatestBlockHash!, Hash(0x99))), cache);
            GloasSlotProcessing.ProcessSlots(state, BoundarySlot + 1, cache);
        }

        SignedBeaconBlockGloas block = MinimalBlock(state, SelfBuildBid(state, state.LatestBlockHash!, Hash(0x9A)));
        PayloadAttestationData data = new() { BeaconBlockRoot = block.Message!.ParentRoot, Slot = state.Slot - 1, PayloadPresent = true, BlobDataAvailable = true };
        block.Message.Body!.PayloadAttestations = [PtcAttestation(state, data, [0, 1, 2], sign: false)];

        Action apply = () => ApplyBlock(state, block, cache);

        if (namesLastFuluSlot)
            Assert.That(apply, Throws.TypeOf<BeaconStateException>().With.Message.Contains("GLOAS_FORK_EPOCH"));
        else
            Assert.That(apply, Throws.Nothing);
    }

    [Test]
    public void GetPtc_reads_the_window_entry_for_the_slots_epoch_and_refuses_slots_outside_it()
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        GloasSlotProcessing.ProcessSlots(state, 2 * SlotsPerEpoch);
        PayloadTimelinessCommittee[] window = state.PtcWindow!;

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(state.GetPtc(63, UpgradeEpochSpec()), Is.SameAs(window[31]), "previous epoch: the first SLOTS_PER_EPOCH entries");
        Assert.That(state.GetPtc(64, UpgradeEpochSpec()), Is.SameAs(window[32]), "current epoch");
        Assert.That(state.GetPtc(96, UpgradeEpochSpec()), Is.SameAs(window[64]), "one epoch of lookahead");
        Assert.That(() => state.GetPtc(31, UpgradeEpochSpec()), Throws.TypeOf<BeaconStateException>(), "two epochs back is outside the window");
        Assert.That(() => state.GetPtc(128, UpgradeEpochSpec()), Throws.TypeOf<BeaconStateException>(), "beyond MIN_SEED_LOOKAHEAD is outside the window");
        Assert.That(() => state.GetPtc(63, SyntheticSpec(gloasForkEpoch: 2)), Throws.TypeOf<BeaconStateException>().With.Message.Contains("GLOAS_FORK_EPOCH"),
            "a slot before GLOAS_FORK_EPOCH has no PTC even inside the window");
    }


    [Test]
    public void ProcessBlock_applies_every_operation_kind_the_body_carries()
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        EpochCache cache = new();
        ApplyBlock(state, MinimalBlock(state, SelfBuildBid(state, state.LatestBlockHash!, Hash(0x99))), cache);
        GloasSlotProcessing.ProcessSlots(state, 33, cache);
        const int slashedProposer = 5;
        const int credentialsChanger = 4;
        Bls.SecretKey fromKey = DeriveKey(500);
        Validator withBlsCredentials = state.Validators![credentialsChanger].Clone();
        withBlsCredentials.WithdrawalCredentials = BlsWithdrawalCredentials(new BlsPublicKey(new Bls.P1(fromKey).Compress()));
        state.Validators[credentialsChanger] = withBlsCredentials;
        CommitteeCache committees = cache.GetCommitteeCache(state, 1);
        Hash256 block32Root = state.GetBlockRootAtSlot(32);
        AttestationGloas attestation = CommitteeAttestation(state, VoteFor(state, slot: 32, targetEpoch: 1, block32Root), committees, committeeIndex: 0, sign: false);

        SignedBeaconBlockGloas block = MinimalBlock(state, SelfBuildBid(state, state.LatestBlockHash!, Hash(0x9A)));
        BeaconBlockBodyGloas body = block.Message!.Body!;
        body.ProposerSlashings = [Equivocation(state, slot: 32, slashedProposer)];
        body.Attestations = [attestation];
        body.BlsToExecutionChanges = [SignedBlsChange(state, credentialsChanger, fromKey, new Address(Hash(0xE7).Bytes[12..]))];
        body.PayloadAttestations = [PtcAttestation(state, new PayloadAttestationData { BeaconBlockRoot = block32Root, Slot = 32, PayloadPresent = true, BlobDataAvailable = true }, [0, 1, 2], sign: false)];

        ApplyBlock(state, block, cache);

        ulong[] attesters = state.GetAttestingIndices(attestation, committees);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(state.Validators[slashedProposer].Slashed, Is.True);
        Assert.That(attesters.Select(i => state.CurrentEpochParticipation![i]), Has.All.EqualTo(0b111));
        Assert.That(state.Validators[credentialsChanger].WithdrawalCredentials!.Bytes[0], Is.EqualTo(Presets.EthWithdrawalPrefix));
    }


    private static readonly ulong ExitEligibleSlot = (Presets.ShardCommitteePeriod + 1) * Presets.SlotsPerEpoch;

    [TestCase("bad signature", "Invalid voluntary exit signature")]
    [TestCase("pending partial withdrawal", "pending partial withdrawals")]
    [TestCase("already exiting", "already initiated an exit")]
    [TestCase("not yet activated", "is not active")]
    [TestCase("index out of range", "is out of range")]
    [TestCase("too recently activated", "not been active long enough")]
    [TestCase("exit epoch in the future", "not valid before epoch")]
    public void ProcessVoluntaryExit_rejects_an_invalid_exit_and_mutates_nothing(string defect, string expectedMessage)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        PubkeyCache pubkeys = InstallRealValidatorKeys(state);
        const int exiting = 9;
        SignedVoluntaryExit exit;
        switch (defect)
        {
            case "too recently activated":
                // Still at the fixture's epoch 1, under SHARD_COMMITTEE_PERIOD since activation.
                state.Slot = BoundarySlot;
                exit = SignedExit(state, exiting, epoch: 1);
                break;
            case "exit epoch in the future":
                state.Slot = ExitEligibleSlot;
                exit = SignedExit(state, exiting, epoch: Presets.ShardCommitteePeriod + 5);
                break;
            case "index out of range":
                state.Slot = ExitEligibleSlot;
                exit = SignedExit(state, ValidatorCount, epoch: 3);
                break;
            default:
                state.Slot = ExitEligibleSlot;
                exit = SignedExit(state, exiting, epoch: 3);
                break;
        }
        switch (defect)
        {
            case "bad signature":
                exit.Signature = Corrupt(exit.Signature);
                break;
            case "not yet activated":
                // Deposited but still queued: the only inactive validator the exit-epoch check does not already catch.
                Validator pending = state.Validators![exiting].Clone();
                pending.ActivationEligibilityEpoch = Presets.FarFutureEpoch;
                pending.ActivationEpoch = Presets.FarFutureEpoch;
                state.Validators[exiting] = pending;
                break;
            case "pending partial withdrawal":
                state.PendingPartialWithdrawals = [new PendingPartialWithdrawal { ValidatorIndex = exiting, Amount = Gwei, WithdrawableEpoch = 1 }];
                break;
            case "already exiting":
                Validator exitingValidator = state.Validators![exiting].Clone();
                exitingValidator.ExitEpoch = Presets.ShardCommitteePeriod + 9;
                state.Validators[exiting] = exitingValidator;
                break;
        }
        AssertRefusedWithoutMutation(state, () =>
            GloasBlockProcessing.ProcessVoluntaryExit(state, exit, new EpochCache(), pubkeys, verifySignature: true), expectedMessage, "a rejected exit must leave the state untouched");
    }


    [TestCase("bad signature", "Invalid BLS to execution change signature")]
    [TestCase("credentials of another key", "does not match the withdrawal credentials")]
    [TestCase("execution credentials already", "does not have BLS withdrawal credentials")]
    [TestCase("index out of range", "is out of range")]
    public void ProcessBlsToExecutionChange_rejects_an_invalid_change_and_mutates_nothing(string defect, string expectedMessage)
    {
        BeaconStateGloas state = CreateGloasState(out _, out _);
        const int changing = 4;
        Bls.SecretKey fromKey = DeriveKey(500);
        Validator validator = state.Validators![changing].Clone();
        validator.WithdrawalCredentials = defect switch
        {
            "credentials of another key" => BlsWithdrawalCredentials(new BlsPublicKey(new Bls.P1(DeriveKey(501)).Compress())),
            "execution credentials already" => EthWithdrawalCredentials(0xAB),
            _ => BlsWithdrawalCredentials(new BlsPublicKey(new Bls.P1(fromKey).Compress())),
        };
        state.Validators[changing] = validator;
        SignedBlsToExecutionChange change = SignedBlsChange(state, defect == "index out of range" ? ValidatorCount : changing, fromKey, new Address(Hash(0xE7).Bytes[12..]));
        if (defect == "bad signature")
            change.Signature = Corrupt(change.Signature);
        AssertRefusedWithoutMutation(state, () =>
            GloasBlockProcessing.ProcessBlsToExecutionChange(state, change, verifySignature: true), expectedMessage, "a rejected change must leave the state untouched");
    }
}
