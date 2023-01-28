// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Shouldly;

namespace Nethermind.BeaconNode.Test.BlockProcessing
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
            byte[] privateKey = TestKeys.PublicKeyToPrivateKey(validator.PublicKey, timeParameters);

            VoluntaryExit voluntaryExit = TestVoluntaryExit.BuildVoluntaryExit(testServiceProvider, currentEpoch, validatorIndex);
            SignedVoluntaryExit signedVoluntaryExit =
                TestVoluntaryExit.SignVoluntaryExit(testServiceProvider, state, voluntaryExit, privateKey);

            RunVoluntaryExitProcessing(testServiceProvider, state, signedVoluntaryExit, expectValid: true);
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
            byte[] privateKey = TestKeys.PublicKeyToPrivateKey(validator.PublicKey, timeParameters);

            VoluntaryExit voluntaryExit = TestVoluntaryExit.BuildVoluntaryExit(testServiceProvider, currentEpoch, validatorIndex);
            SignedVoluntaryExit signedVoluntaryExit = new SignedVoluntaryExit(voluntaryExit, BlsSignature.Zero);

            RunVoluntaryExitProcessing(testServiceProvider, state, signedVoluntaryExit, expectValid: false);
        }

        //    Run ``process_voluntary_exit``, yielding:
        //  - pre-state('pre')
        //  - voluntary_exit('voluntary_exit')
        //  - post-state('post').
        //If ``valid == False``, run expecting ``AssertionError``
        private void RunVoluntaryExitProcessing(IServiceProvider testServiceProvider, BeaconState state, SignedVoluntaryExit signedVoluntaryExit, bool expectValid)
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
                    beaconStateTransition.ProcessVoluntaryExit(state, signedVoluntaryExit);
                });
                return;
            }

            ValidatorIndex validatorIndex = signedVoluntaryExit.Message.ValidatorIndex;

            Epoch preExitEpoch = state.Validators[(int)(ulong)validatorIndex].ExitEpoch;

            beaconStateTransition.ProcessVoluntaryExit(state, signedVoluntaryExit);

            // Assert
            preExitEpoch.ShouldBe(chainConstants.FarFutureEpoch);

            Epoch postExitEpoch = state.Validators[(int)(ulong)validatorIndex].ExitEpoch;
            postExitEpoch.ShouldBeLessThan(chainConstants.FarFutureEpoch);
        }
    }
}
