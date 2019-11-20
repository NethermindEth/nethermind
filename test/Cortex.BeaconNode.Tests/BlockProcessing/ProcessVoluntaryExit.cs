using System;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.BeaconNode.Tests.BlockProcessing
{
    [TestClass]
    public class ProcessVoluntaryExit
    {
        [TestMethod]
        public void SuccessTest()
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
            (var _, var beaconStateAccessor, var _, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            // move state forward PERSISTENT_COMMITTEE_PERIOD epochs to allow for exit
            var timeParameters = timeParameterOptions.CurrentValue;
            var move = timeParameters.SlotsPerEpoch * (ulong)timeParameters.PersistentCommitteePeriod;
            var newSlot = state.Slot + move;
            state.SetSlot(newSlot);

            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            var validatorIndex = beaconStateAccessor.GetActiveValidatorIndices(state, currentEpoch)[0];
            var validator = state.Validators[(int)(ulong)validatorIndex];
            var privateKey = TestKeys.PublicKeyToPrivateKey(validator.PublicKey, timeParameters);

            var voluntaryExit = TestVoluntaryExit.BuildVoluntaryExit(state, currentEpoch, validatorIndex, privateKey, signed: true,
                beaconStateAccessor);

            RunVoluntaryExitProcessing(state, voluntaryExit, expectValid: true, 
                chainConstants,
                beaconStateTransition);
        }

        [TestMethod]
        public void InvalidSignature()
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
            (var _, var beaconStateAccessor, var _, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            // move state forward PERSISTENT_COMMITTEE_PERIOD epochs to allow for exit
            var timeParameters = timeParameterOptions.CurrentValue;
            var move = timeParameters.SlotsPerEpoch * (ulong)timeParameters.PersistentCommitteePeriod;
            var newSlot = state.Slot + move;
            state.SetSlot(newSlot);

            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            var validatorIndex = beaconStateAccessor.GetActiveValidatorIndices(state, currentEpoch)[0];
            var validator = state.Validators[(int)(ulong)validatorIndex];
            var privateKey = TestKeys.PublicKeyToPrivateKey(validator.PublicKey, timeParameters);

            var voluntaryExit = TestVoluntaryExit.BuildVoluntaryExit(state, currentEpoch, validatorIndex, privateKey, signed: false,
                beaconStateAccessor);

            RunVoluntaryExitProcessing(state, voluntaryExit, expectValid: false,
                chainConstants,
                beaconStateTransition);
        }

        //    Run ``process_voluntary_exit``, yielding:
        //  - pre-state('pre')
        //  - voluntary_exit('voluntary_exit')
        //  - post-state('post').
        //If ``valid == False``, run expecting ``AssertionError``
        private void RunVoluntaryExitProcessing(BeaconState state, VoluntaryExit voluntaryExit, bool expectValid,
            ChainConstants chainConstants,
            BeaconStateTransition beaconStateTransition)
        {
            // Act
            if (!expectValid)
            {
                Should.Throw<Exception>(() =>
                {
                    beaconStateTransition.ProcessVoluntaryExit(state, voluntaryExit);
                });
                return;
            }

            var validatorIndex = voluntaryExit.ValidatorIndex;

            var preExitEpoch = state.Validators[(int)(ulong)validatorIndex].ExitEpoch;

            beaconStateTransition.ProcessVoluntaryExit(state, voluntaryExit);

            // Assert
            preExitEpoch.ShouldBe(chainConstants.FarFutureEpoch);

            var postExitEpoch = state.Validators[(int)(ulong)validatorIndex].ExitEpoch;
            postExitEpoch.ShouldBeLessThan(chainConstants.FarFutureEpoch);
        }
    }
}
