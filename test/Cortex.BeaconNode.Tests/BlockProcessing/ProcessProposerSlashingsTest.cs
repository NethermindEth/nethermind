using System;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.BeaconNode.Tests.BlockProcessing
{
    [TestClass]
    public class ProcessProposerSlashingsTest
    {
        [TestMethod]
        public void Success()
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
                out var maxOperationsPerBlockOptions,
                out _);
            (_, var beaconStateAccessor, var _, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            var proposerSlashing = TestProposerSlashing.GetValidProposerSlashing(state, signed1: true, signed2: true,
                timeParameterOptions.CurrentValue,
                beaconStateAccessor);

            RunProposerSlashingProcessing(state, proposerSlashing, expectValid: true,
                chainConstants: chainConstants,
                beaconStateTransition: beaconStateTransition);
        }

        [TestMethod]
        public void InvalidSignature1()
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
                out var maxOperationsPerBlockOptions,
                out _);
            (_, var beaconStateAccessor, var _, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            var proposerSlashing = TestProposerSlashing.GetValidProposerSlashing(state, signed1: false, signed2: true,
                timeParameterOptions.CurrentValue,
                beaconStateAccessor);

            RunProposerSlashingProcessing(state, proposerSlashing, expectValid: false,
                chainConstants: chainConstants,
                beaconStateTransition: beaconStateTransition);
        }

        //Run ``process_proposer_slashing``, yielding:
        //- pre-state('pre')
        //- proposer_slashing('proposer_slashing')
        //- post-state('post').
        //If ``valid == False``, run expecting ``AssertionError``
        private void RunProposerSlashingProcessing(BeaconState state, ProposerSlashing proposerSlashing, bool expectValid,
            ChainConstants chainConstants,
            BeaconStateTransition beaconStateTransition)
        {
            if (!expectValid)
            {
                Should.Throw<Exception>(() =>
                {
                    beaconStateTransition.ProcessProposerSlashing(state, proposerSlashing);
                });
                return;
            }

            var preProposerBalance = TestState.GetBalance(state, proposerSlashing.ProposerIndex);

            beaconStateTransition.ProcessProposerSlashing(state, proposerSlashing);

            var slashedValidator = state.Validators[(int)(ulong)proposerSlashing.ProposerIndex];
            slashedValidator.IsSlashed.ShouldBeTrue();
            slashedValidator.ExitEpoch.ShouldBeLessThan(chainConstants.FarFutureEpoch);
            slashedValidator.WithdrawableEpoch.ShouldBeLessThan(chainConstants.FarFutureEpoch);

            var balance = TestState.GetBalance(state, proposerSlashing.ProposerIndex);
            balance.ShouldBeLessThan(preProposerBalance);
        }
    }
}
