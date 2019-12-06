using System;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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
            var testServiceProvider = TestSystem.BuildTestServiceProvider();
            var state = TestState.PrepareTestState(testServiceProvider);

            var attesterSlashing = TestAttesterSlashing.GetValidAttesterSlashing(testServiceProvider, state, signed1: true, signed2: true);

            RunAttesterSlashingProcessing(testServiceProvider, state, attesterSlashing, expectValid: true);
        }

        [TestMethod]
        public void InvalidSignature1()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider();
            var state = TestState.PrepareTestState(testServiceProvider);

            var attesterSlashing = TestAttesterSlashing.GetValidAttesterSlashing(testServiceProvider, state, signed1: false, signed2: true);

            RunAttesterSlashingProcessing(testServiceProvider, state, attesterSlashing, expectValid: false);
        }

        //    Run ``process_attester_slashing``, yielding:
        //  - pre-state('pre')
        //  - attester_slashing('attester_slashing')
        //  - post-state('post').
        //If ``valid == False``, run expecting ``AssertionError``
        private void RunAttesterSlashingProcessing(IServiceProvider testServiceProvider, BeaconState state, AttesterSlashing attesterSlashing, bool expectValid)
        {
            var chainConstants = testServiceProvider.GetService<ChainConstants>();
            var stateListLengths = testServiceProvider.GetService<IOptions<StateListLengths>>().Value;
            var rewardsAndPenalties = testServiceProvider.GetService<IOptions<RewardsAndPenalties>>().Value;

            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            var beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

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
                Console.WriteLine($"Checking index {slashedIndex}");
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
