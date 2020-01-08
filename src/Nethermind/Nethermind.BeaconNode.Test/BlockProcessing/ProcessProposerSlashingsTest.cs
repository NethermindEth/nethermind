//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2.Containers;
using Shouldly;

namespace Nethermind.BeaconNode.Test.BlockProcessing
{
    [TestClass]
    public class ProcessProposerSlashingsTest
    {
        [TestMethod]
        public void Success()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider();
            var state = TestState.PrepareTestState(testServiceProvider);

            var proposerSlashing = TestProposerSlashing.GetValidProposerSlashing(testServiceProvider, state, signed1: true, signed2: true);

            RunProposerSlashingProcessing(testServiceProvider, state, proposerSlashing, expectValid: true);
        }

        [TestMethod]
        public void InvalidSignature1()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider();
            var state = TestState.PrepareTestState(testServiceProvider);

            var proposerSlashing = TestProposerSlashing.GetValidProposerSlashing(testServiceProvider, state, signed1: false, signed2: true);

            RunProposerSlashingProcessing(testServiceProvider, state, proposerSlashing, expectValid: false);
        }

        //Run ``process_proposer_slashing``, yielding:
        //- pre-state('pre')
        //- proposer_slashing('proposer_slashing')
        //- post-state('post').
        //If ``valid == False``, run expecting ``AssertionError``
        private void RunProposerSlashingProcessing(IServiceProvider testServiceProvider, BeaconState state, ProposerSlashing proposerSlashing, bool expectValid)
        {
            var chainConstants = testServiceProvider.GetService<ChainConstants>();
            var beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

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
