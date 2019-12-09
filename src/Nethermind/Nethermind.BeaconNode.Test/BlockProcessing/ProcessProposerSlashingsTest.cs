using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Tests.Helpers;
using Shouldly;

namespace Nethermind.BeaconNode.Tests.BlockProcessing
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
