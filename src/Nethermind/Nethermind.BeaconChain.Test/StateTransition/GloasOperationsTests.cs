// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.StateTransition;

/// <summary>
/// The Gloas block-body operations (<see cref="GloasBlockProcessing.ProcessOperations"/>), each
/// exercised with real BLS signatures against the fixture chain, and - for every step the pinned
/// spec left unchanged - differentially against the vector-tested Fulu pipeline. There are no
/// Gloas operations vectors at the pinned consensus-specs tag, so these hand-written cases are the
/// only oracle; each one asserts a state change the operation must make, never just the absence
/// of a throw.
/// </summary>
public class GloasOperationsTests
{
    private static readonly ulong SlotsPerEpoch = Presets.SlotsPerEpoch;

    // ---- Proposer slashings ----

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
        Assert.Multiple(() =>
        {
            Assert.That(slashed.Slashed, Is.True);
            Assert.That(slashed.ExitEpoch, Is.Not.EqualTo(Presets.FarFutureEpoch), "slashing must initiate the exit");
            Assert.That(slashed.WithdrawableEpoch, Is.EqualTo(state.GetCurrentEpoch() + Presets.EpochsPerSlashingsVector));
            Assert.That(state.Slashings![(int)state.GetCurrentEpoch()], Is.EqualTo(effectiveBalance));
            Assert.That(state.Balances[proposer], Is.EqualTo(proposerBalanceBefore - effectiveBalance / Presets.MinSlashingPenaltyQuotientElectra));
            Assert.That(state.Balances[whistleblower], Is.EqualTo(whistleblowerBalanceBefore + effectiveBalance / Presets.WhistleblowerRewardQuotientElectra));
            Assert.That(state.BuilderPendingPayments[32].Withdrawal!.Amount, Is.EqualTo(0ul), "the equivocating proposer's payment must be cleared");
            Assert.That(state.BuilderPendingPayments[32].ProposerIndex, Is.EqualTo(0ul));
        });
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

        Assert.Multiple(() =>
        {
            Assert.That(state.Validators![proposer].Slashed, Is.True);
            Assert.That(state.BuilderPendingPayments[0].Withdrawal!.Amount, Is.EqualTo(0ul), "the previous-epoch payment must be cleared through its rotated address");
        });
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

        Assert.Multiple(() =>
        {
            Assert.That(state.Validators![bystander].Slashed, Is.True);
            Assert.That(state.BuilderPendingPayments![32].Withdrawal!.Amount, Is.EqualTo(bidValue), "an unrelated equivocation must not grief the honest proposer's payment");
            Assert.That(state.BuilderPendingPayments[32].ProposerIndex, Is.EqualTo((ulong)proposer));
        });
    }

    [TestCase("identical headers", "identical")]
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
            case "bad signature":
                slashing.SignedHeader2!.Signature = Corrupt(slashing.SignedHeader2.Signature);
                break;
            case "already slashed":
                Validator alreadySlashed = state.Validators![proposer].Clone();
                alreadySlashed.Slashed = true;
                state.Validators[proposer] = alreadySlashed;
                break;
        }
        Hash256 rootBefore = SszRoots.HashTreeRoot(state);

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            GloasBlockProcessing.ProcessProposerSlashing(state, slashing, new EpochCache(), pubkeys, verifySignatures: true))!;

        Assert.That(ex.Message, Does.Contain(expectedMessage));
        Assert.That(SszRoots.HashTreeRoot(state), Is.EqualTo(rootBefore), "a rejected slashing must leave the state untouched");
    }

    // ---- Attester slashings ----

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

        Assert.Multiple(() =>
        {
            Assert.That(state.Validators!.Select(v => v.Slashed).Take(6), Is.EqualTo(new[] { false, false, true, true, false, false }).AsCollection);
            Assert.That(state.Balances[2], Is.EqualTo(balanceBefore - 32 * Gwei / Presets.MinSlashingPenaltyQuotientElectra));
            Assert.That(state.Slashings![1], Is.EqualTo(2 * 32 * Gwei));
        });
    }

    [TestCase("no intersection", "slashed no validator")]
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
            "same vote twice" => new() { Attestation1 = SignedIndexedAttestation(state, vote1, [1, 2]), Attestation2 = SignedIndexedAttestation(state, vote1, [2, 3]) },
            "unsorted indices" => new() { Attestation1 = SignedIndexedAttestation(state, vote1, [2, 1]), Attestation2 = SignedIndexedAttestation(state, vote2, [2, 3]) },
            "bad signature" => new() { Attestation1 = SignedIndexedAttestation(state, vote1, [1, 2]), Attestation2 = SignedIndexedAttestation(state, vote2, [2, 3]) },
            _ => throw new ArgumentOutOfRangeException(nameof(defect)),
        };
        if (defect == "bad signature")
            slashing.Attestation2!.Signature = Corrupt(slashing.Attestation2.Signature);
        Hash256 rootBefore = SszRoots.HashTreeRoot(state);

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            GloasBlockProcessing.ProcessAttesterSlashing(state, slashing, new EpochCache(), pubkeys, verifySignatures: true))!;

        Assert.That(ex.Message, Does.Contain(expectedMessage));
        Assert.That(SszRoots.HashTreeRoot(state), Is.EqualTo(rootBefore), "a rejected slashing must leave the state untouched");
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

        Assert.Multiple(() =>
        {
            Assert.That(GloasBlockProcessing.IsValidIndexedAttestation(state, WithIndices(bound), new PubkeyCache(), verifySignature: false), Is.True);
            Assert.That(GloasBlockProcessing.IsValidIndexedAttestation(state, WithIndices(bound + 1), new PubkeyCache(), verifySignature: false), Is.False);
        });
    }

    // ---- Voluntary exits ----

    /// <summary>Well past SHARD_COMMITTEE_PERIOD, so a genesis-activated validator may exit; the block-root window still covers the epoch boundary.</summary>
    private static readonly ulong ExitEligibleSlot = (Presets.ShardCommitteePeriod + 1) * Presets.SlotsPerEpoch;

    [Test]
    public void ProcessVoluntaryExit_initiates_the_exit_and_queues_it_where_the_fulu_pipeline_would()
    {
        BeaconStateFulu pre = CreateFuluStateAtBoundary(ValidatorCount);
        BeaconStateFulu fulu = pre.Clone();
        BeaconStateGloas gloas = GloasForkTransition.UpgradeToGloas(pre, SyntheticSpec());
        PubkeyCache pubkeys = InstallRealValidatorKeys(gloas);
        fulu.Slot = ExitEligibleSlot;
        gloas.Slot = ExitEligibleSlot;
        const int exiting = 9;
        SignedVoluntaryExit exit = SignedExit(gloas, exiting, epoch: 3);

        GloasBlockProcessing.ProcessVoluntaryExit(gloas, exit, new EpochCache(), pubkeys, verifySignature: true);
        // The Fulu twin still carries placeholder pubkeys; only the queueing is compared, not the signature.
        BlockProcessing.ProcessVoluntaryExit(fulu, exit, new EpochCache(), new PubkeyCache(), verifySignature: false);

        Assert.Multiple(() =>
        {
            Assert.That(gloas.Validators![exiting].ExitEpoch, Is.EqualTo(BeaconStateAccessors.ComputeActivationExitEpoch(Presets.ShardCommitteePeriod + 1)));
            Assert.That(gloas.Validators[exiting].ExitEpoch, Is.EqualTo(fulu.Validators![exiting].ExitEpoch));
            Assert.That(gloas.Validators[exiting].WithdrawableEpoch, Is.EqualTo(fulu.Validators[exiting].WithdrawableEpoch));
            Assert.That(gloas.EarliestExitEpoch, Is.EqualTo(fulu.EarliestExitEpoch));
            Assert.That(gloas.ExitBalanceToConsume, Is.EqualTo(fulu.ExitBalanceToConsume));
        });
    }

    [TestCase("bad signature", "Invalid voluntary exit signature")]
    [TestCase("pending partial withdrawal", "pending partial withdrawals")]
    [TestCase("already exiting", "already initiated an exit")]
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
            case "pending partial withdrawal":
                state.PendingPartialWithdrawals = [new PendingPartialWithdrawal { ValidatorIndex = exiting, Amount = Gwei, WithdrawableEpoch = 1 }];
                break;
            case "already exiting":
                Validator exitingValidator = state.Validators![exiting].Clone();
                exitingValidator.ExitEpoch = Presets.ShardCommitteePeriod + 9;
                state.Validators[exiting] = exitingValidator;
                break;
        }
        Hash256 rootBefore = SszRoots.HashTreeRoot(state);

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            GloasBlockProcessing.ProcessVoluntaryExit(state, exit, new EpochCache(), pubkeys, verifySignature: true))!;

        Assert.That(ex.Message, Does.Contain(expectedMessage));
        Assert.That(SszRoots.HashTreeRoot(state), Is.EqualTo(rootBefore), "a rejected exit must leave the state untouched");
    }

    // ---- BLS-to-execution changes ----

    [Test]
    public void ProcessBlsToExecutionChange_rewrites_the_bls_credentials_to_the_execution_address_as_the_fulu_pipeline_does()
    {
        BeaconStateFulu pre = CreateFuluStateAtBoundary(ValidatorCount);
        const int changing = 4;
        Bls.SecretKey fromKey = DeriveKey(500);
        Validator withBlsCredentials = pre.Validators![changing].Clone();
        withBlsCredentials.WithdrawalCredentials = BlsWithdrawalCredentials(new BlsPublicKey(new Bls.P1(fromKey).Compress()));
        pre.Validators[changing] = withBlsCredentials;
        BeaconStateFulu fulu = pre.Clone();
        BeaconStateGloas gloas = GloasForkTransition.UpgradeToGloas(pre, SyntheticSpec());
        Address toAddress = new(Hash(0xE7).Bytes[12..]);
        SignedBlsToExecutionChange change = SignedBlsChange(gloas, changing, fromKey, toAddress);

        GloasBlockProcessing.ProcessBlsToExecutionChange(gloas, change, verifySignature: true);
        BlockProcessing.ProcessBlsToExecutionChange(fulu, change, verifySignature: true);

        byte[] expectedBytes = new byte[32];
        expectedBytes[0] = Presets.EthWithdrawalPrefix;
        toAddress.Bytes.CopyTo(expectedBytes.AsSpan(12));
        Hash256 expected = new(expectedBytes);
        Assert.Multiple(() =>
        {
            Assert.That(gloas.Validators![changing].WithdrawalCredentials, Is.EqualTo(expected));
            Assert.That(gloas.Validators[changing].WithdrawalCredentials, Is.EqualTo(fulu.Validators![changing].WithdrawalCredentials));
        });
    }

    [TestCase("bad signature", "Invalid BLS to execution change signature")]
    [TestCase("credentials of another key", "does not match the withdrawal credentials")]
    [TestCase("execution credentials already", "does not have BLS withdrawal credentials")]
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
        SignedBlsToExecutionChange change = SignedBlsChange(state, changing, fromKey, new Address(Hash(0xE7).Bytes[12..]));
        if (defect == "bad signature")
            change.Signature = Corrupt(change.Signature);
        Hash256 rootBefore = SszRoots.HashTreeRoot(state);

        BeaconStateException ex = Assert.Throws<BeaconStateException>(() =>
            GloasBlockProcessing.ProcessBlsToExecutionChange(state, change, verifySignature: true))!;

        Assert.That(ex.Message, Does.Contain(expectedMessage));
        Assert.That(SszRoots.HashTreeRoot(state), Is.EqualTo(rootBefore), "a rejected change must leave the state untouched");
    }

    [Test]
    public void SlashValidator_applies_the_same_exit_penalty_and_rewards_as_the_fulu_mutator()
    {
        BeaconStateFulu pre = CreateFuluStateAtBoundary(ValidatorCount);
        BeaconStateFulu fulu = pre.Clone();
        BeaconStateGloas gloas = GloasForkTransition.UpgradeToGloas(pre, SyntheticSpec());
        const int slashed = 3;
        const int proposer = 7;
        fulu.ProposerLookahead![0] = proposer;
        gloas.ProposerLookahead![0] = proposer;

        // At this fixture size both forks' exit churn floors at MIN_PER_EPOCH_CHURN_LIMIT_ELECTRA,
        // so the EIP-8061 exit churn and the Electra activation-exit churn agree on the exit epoch.
        fulu.SlashValidator(slashed, new EpochCache());
        gloas.SlashValidator(slashed, new EpochCache());

        Assert.Multiple(() =>
        {
            Assert.That(gloas.Validators![slashed].Slashed, Is.True);
            Assert.That(gloas.Validators[slashed].ExitEpoch, Is.EqualTo(fulu.Validators![slashed].ExitEpoch));
            Assert.That(gloas.Validators[slashed].WithdrawableEpoch, Is.EqualTo(fulu.Validators[slashed].WithdrawableEpoch));
            Assert.That(gloas.Balances, Is.EqualTo(fulu.Balances).AsCollection);
            Assert.That(gloas.Balances![slashed], Is.LessThan(32 * Gwei), "fixture bug: the penalty must actually have been applied");
            Assert.That(gloas.Balances[proposer], Is.GreaterThan(32 * Gwei), "fixture bug: the reward must actually have been credited");
            Assert.That(gloas.Slashings, Is.EqualTo(fulu.Slashings).AsCollection);
            Assert.That(gloas.EarliestExitEpoch, Is.EqualTo(fulu.EarliestExitEpoch));
            Assert.That(gloas.ExitBalanceToConsume, Is.EqualTo(fulu.ExitBalanceToConsume));
        });
    }
}
