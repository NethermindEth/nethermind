using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Tests.Helpers;
using Nethermind.Core2.Types;
using Shouldly;

namespace Nethermind.BeaconNode.Tests.BlockProcessing
{
    [TestClass]
    public class ProcessAttesterSlashingsTest
    {
        [TestMethod]
        public void SuccessDouble()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            AttesterSlashing attesterSlashing = TestAttesterSlashing.GetValidAttesterSlashing(testServiceProvider, state, signed1: true, signed2: true);

            RunAttesterSlashingProcessing(testServiceProvider, state, attesterSlashing, expectValid: true);
        }

        [TestMethod]
        public void InvalidSignature1()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            AttesterSlashing attesterSlashing = TestAttesterSlashing.GetValidAttesterSlashing(testServiceProvider, state, signed1: false, signed2: true);

            RunAttesterSlashingProcessing(testServiceProvider, state, attesterSlashing, expectValid: false);
        }

        //    Run ``process_attester_slashing``, yielding:
        //  - pre-state('pre')
        //  - attester_slashing('attester_slashing')
        //  - post-state('post').
        //If ``valid == False``, run expecting ``AssertionError``
        private void RunAttesterSlashingProcessing(IServiceProvider testServiceProvider, BeaconState state, AttesterSlashing attesterSlashing, bool expectValid)
        {
            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            StateListLengths stateListLengths = testServiceProvider.GetService<IOptions<StateListLengths>>().Value;
            RewardsAndPenalties rewardsAndPenalties = testServiceProvider.GetService<IOptions<RewardsAndPenalties>>().Value;

            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            BeaconStateTransition beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

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

            ValidatorIndex proposerIndex = beaconStateAccessor.GetBeaconProposerIndex(state);
            Gwei preProposerBalance = TestState.GetBalance(state, proposerIndex);
            var preSlashings = slashedIndices.ToDictionary(x => x, x => TestState.GetBalance(state, x));
            var preWithdrawableEpochs = slashedIndices.ToDictionary(x => x, x => state.Validators[(int)(ulong)x].WithdrawableEpoch);

            Gwei totalProposerRewards = preSlashings.Values.Aggregate(Gwei.Zero, (sum, x) => sum + x / rewardsAndPenalties.WhistleblowerRewardQuotient);

            // Process slashing
            beaconStateTransition.ProcessAttesterSlashing(state, attesterSlashing);

            foreach (ValidatorIndex slashedIndex in slashedIndices)
            {
                Console.WriteLine($"Checking index {slashedIndex}");
                Epoch preWithdrawableEpoch = preWithdrawableEpochs[slashedIndex];
                Validator slashedValidator = state.Validators[(int)(ulong)slashedIndex];

                // Check Slashing
                slashedValidator.IsSlashed.ShouldBeTrue();
                slashedValidator.ExitEpoch.ShouldBeLessThan(chainConstants.FarFutureEpoch);
                if (preWithdrawableEpoch < chainConstants.FarFutureEpoch)
                {
                    ulong slashingsEpoch = beaconStateAccessor.GetCurrentEpoch(state) + stateListLengths.EpochsPerSlashingsVector;
                    Epoch expectedWithdrawableEpoch = Epoch.Max(preWithdrawableEpoch, (Epoch)slashingsEpoch);
                    slashedValidator.WithdrawableEpoch.ShouldBe(expectedWithdrawableEpoch);
                }
                else
                {
                    slashedValidator.WithdrawableEpoch.ShouldBeLessThan(chainConstants.FarFutureEpoch);
                }

                Gwei preSlashing = preSlashings[slashedIndex];
                Gwei slashedBalance = TestState.GetBalance(state, slashedIndex);
                slashedBalance.ShouldBeLessThan(preSlashing);
            }

            if (!slashedIndices.Contains(proposerIndex))
            {
                // gained whistleblower reward
                Gwei expectedProposerBalance = preProposerBalance + totalProposerRewards;
                Gwei proposerBalance = TestState.GetBalance(state, proposerIndex);
                proposerBalance.ShouldBe(expectedProposerBalance);
            }
            else
            {
                // gained rewards for all slashings, which may include others. And only lost that of themselves.
                Gwei expectedProposerBalance = preProposerBalance + totalProposerRewards
                    - (preSlashings[proposerIndex] / rewardsAndPenalties.MinimumSlashingPenaltyQuotient);
                Gwei proposerBalance = TestState.GetBalance(state, proposerIndex);
                proposerBalance.ShouldBe(expectedProposerBalance);
            }
        }
    }
}
