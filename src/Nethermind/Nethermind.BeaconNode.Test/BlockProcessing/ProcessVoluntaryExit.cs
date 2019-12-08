﻿using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Tests.Helpers;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Types;
using Shouldly;
using BeaconState = Nethermind.BeaconNode.Containers.BeaconState;
using Validator = Nethermind.BeaconNode.Containers.Validator;

namespace Nethermind.BeaconNode.Tests.BlockProcessing
{
    [TestClass]
    public class ProcessVoluntaryExit
    {
        [TestMethod]
        public void SuccessTest()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            // move state forward PERSISTENT_COMMITTEE_PERIOD epochs to allow for exit
            ulong move = timeParameters.SlotsPerEpoch * (ulong)timeParameters.PersistentCommitteePeriod;
            ulong newSlot = state.Slot + move;
            state.SetSlot((Slot)newSlot);

            Epoch currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            ValidatorIndex validatorIndex = beaconStateAccessor.GetActiveValidatorIndices(state, currentEpoch)[0];
            Validator validator = state.Validators[(int)(ulong)validatorIndex];
            var privateKey = TestKeys.PublicKeyToPrivateKey(validator.PublicKey, timeParameters);

            VoluntaryExit voluntaryExit = TestVoluntaryExit.BuildVoluntaryExit(testServiceProvider, state, currentEpoch, validatorIndex, privateKey, signed: true);

            RunVoluntaryExitProcessing(testServiceProvider, state, voluntaryExit, expectValid: true);
        }

        [TestMethod]
        public void InvalidSignature()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            // move state forward PERSISTENT_COMMITTEE_PERIOD epochs to allow for exit
            ulong move = timeParameters.SlotsPerEpoch * (ulong)timeParameters.PersistentCommitteePeriod;
            ulong newSlot = state.Slot + move;
            state.SetSlot((Slot)newSlot);

            Epoch currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            ValidatorIndex validatorIndex = beaconStateAccessor.GetActiveValidatorIndices(state, currentEpoch)[0];
            Validator validator = state.Validators[(int)(ulong)validatorIndex];
            var privateKey = TestKeys.PublicKeyToPrivateKey(validator.PublicKey, timeParameters);

            VoluntaryExit voluntaryExit = TestVoluntaryExit.BuildVoluntaryExit(testServiceProvider, state, currentEpoch, validatorIndex, privateKey, signed: false);

            RunVoluntaryExitProcessing(testServiceProvider, state, voluntaryExit, expectValid: false);
        }

        //    Run ``process_voluntary_exit``, yielding:
        //  - pre-state('pre')
        //  - voluntary_exit('voluntary_exit')
        //  - post-state('post').
        //If ``valid == False``, run expecting ``AssertionError``
        private void RunVoluntaryExitProcessing(IServiceProvider testServiceProvider, BeaconState state, VoluntaryExit voluntaryExit, bool expectValid)
        {
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            MaxOperationsPerBlock maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            BeaconStateTransition beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            // Act
            if (!expectValid)
            {
                Should.Throw<Exception>(() =>
                {
                    beaconStateTransition.ProcessVoluntaryExit(state, voluntaryExit);
                });
                return;
            }

            ValidatorIndex validatorIndex = voluntaryExit.ValidatorIndex;

            Epoch preExitEpoch = state.Validators[(int)(ulong)validatorIndex].ExitEpoch;

            beaconStateTransition.ProcessVoluntaryExit(state, voluntaryExit);

            // Assert
            preExitEpoch.ShouldBe(chainConstants.FarFutureEpoch);

            Epoch postExitEpoch = state.Validators[(int)(ulong)validatorIndex].ExitEpoch;
            postExitEpoch.ShouldBeLessThan(chainConstants.FarFutureEpoch);
        }
    }
}
