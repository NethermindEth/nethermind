using System;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.BeaconNode.Tests.BlockProcessing
{
    [TestClass]
    public class ProcessAttesterSlashingsTest
    {
        [TestMethod]
        public void SuccessDouble()
        {
            // Arrange
            TestConfiguration.GetMinimalConfiguration(
                out var chainConstants,
                out var miscellaneousParameterOptions,
                out var gweiValueOptions,
                out var initialValueOptions,
                out var timeParameterOptions,
                out var stateListLengthOptions,
                out var rewardsAndPenaltiesOptions,
                out var maxOperationsPerBlockOptions);
            (var beaconChainUtility, var beaconStateAccessor, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            var attesterSlashing = TestAttesterSlashing.GetValidAttesterSlashing(state, signed1: true, signed2: true,
                miscellaneousParameterOptions.CurrentValue, timeParameterOptions.CurrentValue, stateListLengthOptions.CurrentValue, maxOperationsPerBlockOptions.CurrentValue,
                beaconChainUtility, beaconStateAccessor, beaconStateTransition);

            RunAttesterSlashingProcessing(state, attesterSlashing, expectValid: true,
                chainConstants, stateListLengthOptions.CurrentValue, rewardsAndPenaltiesOptions.CurrentValue,
                beaconStateAccessor, beaconStateTransition);
        }

        //    Run ``process_attester_slashing``, yielding:
        //  - pre-state('pre')
        //  - attester_slashing('attester_slashing')
        //  - post-state('post').
        //If ``valid == False``, run expecting ``AssertionError``
        private void RunAttesterSlashingProcessing(BeaconState state, AttesterSlashing attesterSlashing, bool expectValid,
            ChainConstants chainConstants, StateListLengths stateListLengths, RewardsAndPenalties rewardsAndPenalties,
            BeaconStateAccessor beaconStateAccessor, BeaconStateTransition beaconStateTransition)
        {
            if (!expectValid)
            {
                Should.Throw<Exception>(() =>
                {
                    beaconStateTransition.ProcessAttesterSlashing(state, attesterSlashing);
                });
                return;
            }

            var slashedIndices = attesterSlashing.Attestation1.CustodyBit0Indices
                .Union(attesterSlashing.Attestation1.CustodyBit1Indices)
                .ToList();

            var proposerIndex = beaconStateAccessor.GetBeaconProposerIndex(state);
            var preProposerBalance = TestState.GetBalance(state, proposerIndex);
            var preSlashings = slashedIndices.ToDictionary(x => x, x => TestState.GetBalance(state, x));
            var preWithdrawableEpochs = slashedIndices.ToDictionary(x => x, x => state.Validators[(int)(ulong)x].WithdrawableEpoch);

            var totalProposerRewards = preSlashings.Values.Aggregate(Gwei.Zero, (sum, x) => sum + x / rewardsAndPenalties.WhistleblowerRewardQuotient);

            // Process slashing
            beaconStateTransition.ProcessAttesterSlashing(state, attesterSlashing);

            foreach (var slashedIndex in slashedIndices)
            {
                Console.Write($"Checking index {slashedIndex}");
                var preWithdrawableEpoch = preWithdrawableEpochs[slashedIndex];
                var slashedValidator = state.Validators[(int)(ulong)slashedIndex];

                // Check Slashing
                slashedValidator.IsSlashed.ShouldBeTrue();
                slashedValidator.ExitEpoch.ShouldBeLessThan(chainConstants.FarFutureEpoch);
                if (preWithdrawableEpoch < chainConstants.FarFutureEpoch)
                {
                    var slashingsEpoch = beaconStateAccessor.GetCurrentEpoch(state) + stateListLengths.EpochsPerSlashingsVector;
                    var expectedWithdrawableEpoch = Epoch.Max(preWithdrawableEpoch, slashingsEpoch);
                    slashedValidator.WithdrawableEpoch.ShouldBe(expectedWithdrawableEpoch);
                }
                else
                {
                    slashedValidator.WithdrawableEpoch.ShouldBeLessThan(chainConstants.FarFutureEpoch);
                }

                var preSlashing = preSlashings[slashedIndex];
                var slashedBalance = TestState.GetBalance(state, slashedIndex);
                slashedBalance.ShouldBeLessThan(preSlashing);
            }

            if (!slashedIndices.Contains(proposerIndex))
            {
                // gained whistleblower reward
                var expectedProposerBalance = preProposerBalance + totalProposerRewards;
                var proposerBalance = TestState.GetBalance(state, proposerIndex);
                proposerBalance.ShouldBe(expectedProposerBalance);
            }
            else
            {
                // gained rewards for all slashings, which may include others. And only lost that of themselves.
                var expectedProposerBalance = preProposerBalance + totalProposerRewards 
                    - (preSlashings[proposerIndex] / rewardsAndPenalties.MinimumSlashingPenaltyQuotient);
                var proposerBalance = TestState.GetBalance(state, proposerIndex);
                proposerBalance.ShouldBe(expectedProposerBalance);
            }
        }
    }
}
