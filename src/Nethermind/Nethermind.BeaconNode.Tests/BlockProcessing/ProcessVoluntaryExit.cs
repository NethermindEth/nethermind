using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Tests.Helpers;
using Shouldly;

namespace Nethermind.BeaconNode.Tests.BlockProcessing
{
    [TestClass]
    public class ProcessVoluntaryExit
    {
        [TestMethod]
        public void SuccessTest()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider();
            var state = TestState.PrepareTestState(testServiceProvider);

            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            // move state forward PERSISTENT_COMMITTEE_PERIOD epochs to allow for exit
            var move = timeParameters.SlotsPerEpoch * (ulong)timeParameters.PersistentCommitteePeriod;
            var newSlot = state.Slot + move;
            state.SetSlot(newSlot);

            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            var validatorIndex = beaconStateAccessor.GetActiveValidatorIndices(state, currentEpoch)[0];
            var validator = state.Validators[(int)(ulong)validatorIndex];
            var privateKey = TestKeys.PublicKeyToPrivateKey(validator.PublicKey, timeParameters);

            var voluntaryExit = TestVoluntaryExit.BuildVoluntaryExit(testServiceProvider, state, currentEpoch, validatorIndex, privateKey, signed: true);

            RunVoluntaryExitProcessing(testServiceProvider, state, voluntaryExit, expectValid: true);
        }

        [TestMethod]
        public void InvalidSignature()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider();
            var state = TestState.PrepareTestState(testServiceProvider);

            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            // move state forward PERSISTENT_COMMITTEE_PERIOD epochs to allow for exit
            var move = timeParameters.SlotsPerEpoch * (ulong)timeParameters.PersistentCommitteePeriod;
            var newSlot = state.Slot + move;
            state.SetSlot(newSlot);

            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            var validatorIndex = beaconStateAccessor.GetActiveValidatorIndices(state, currentEpoch)[0];
            var validator = state.Validators[(int)(ulong)validatorIndex];
            var privateKey = TestKeys.PublicKeyToPrivateKey(validator.PublicKey, timeParameters);

            var voluntaryExit = TestVoluntaryExit.BuildVoluntaryExit(testServiceProvider, state, currentEpoch, validatorIndex, privateKey, signed: false);

            RunVoluntaryExitProcessing(testServiceProvider, state, voluntaryExit, expectValid: false);
        }

        //    Run ``process_voluntary_exit``, yielding:
        //  - pre-state('pre')
        //  - voluntary_exit('voluntary_exit')
        //  - post-state('post').
        //If ``valid == False``, run expecting ``AssertionError``
        private void RunVoluntaryExitProcessing(IServiceProvider testServiceProvider, BeaconState state, VoluntaryExit voluntaryExit, bool expectValid)
        {
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            var chainConstants = testServiceProvider.GetService<ChainConstants>();
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            var beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

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
