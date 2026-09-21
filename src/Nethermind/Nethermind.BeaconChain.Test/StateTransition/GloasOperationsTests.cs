// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
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
