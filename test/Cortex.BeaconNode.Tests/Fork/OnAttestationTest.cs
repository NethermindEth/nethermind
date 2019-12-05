using System;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Storage;
using Cortex.BeaconNode.Ssz;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;
using System.Linq;

namespace Cortex.BeaconNode.Tests.Fork
{
    [TestClass]
    public class OnAttestationTest
    {
        [TestMethod]
        public void OnAttestationCurrentEpoch()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);
            var state = TestState.PrepareTestState(testServiceProvider);

            var initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var forkChoice = testServiceProvider.GetService<ForkChoice>();

            // Initialization
            var store = forkChoice.GetGenesisStore(state);
            var time = store.Time + timeParameters.SecondsPerSlot * 2;
            forkChoice.OnTick(store, time);

            var block = TestBlock.BuildEmptyBlockForNextSlot(testServiceProvider, state, signed: true);
            TestState.StateTransitionAndSignBlock(testServiceProvider, state, block);

            // Store block in store
            forkChoice.OnBlock(store, block);

            var attestation = TestAttestation.GetValidAttestation(testServiceProvider, state, block.Slot, CommitteeIndex.None, signed: true);

            attestation.Data.Target.Epoch.ShouldBe(initialValues.GenesisEpoch);

            var beaconChainUtility = testServiceProvider.GetService<BeaconChainUtility>();
            var currentSlot = forkChoice.GetCurrentSlot(store);
            var currentEpoch = beaconChainUtility.ComputeEpochAtSlot(currentSlot);
            currentEpoch.ShouldBe(initialValues.GenesisEpoch);

            RunOnAttestation(testServiceProvider, state, store, attestation, expectValid: true);
        }

        private void RunOnAttestation(IServiceProvider testServiceProvider, BeaconState state, IStore store, Attestation attestation, bool expectValid)
        {
            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var maxOperationsPerBlock = testServiceProvider.GetService<IOptions<MaxOperationsPerBlock>>().Value;

            var forkChoice = testServiceProvider.GetService<ForkChoice>();

            if (!expectValid)
            {
                Should.Throw<Exception>(() =>
                {
                    forkChoice.OnAttestation(store, attestation);
                });
                return;
            }

            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            var indexedAttestation = beaconStateAccessor.GetIndexedAttestation(state, attestation);
            forkChoice.OnAttestation(store, attestation);

            var attestingIndices = beaconStateAccessor.GetAttestingIndices(state, attestation.Data, attestation.AggregationBits);
            var firstAttestingIndex = attestingIndices.First();
            var latestMessage = store.LatestMessages[firstAttestingIndex];

            latestMessage.Epoch.ShouldBe(attestation.Data.Target.Epoch);
            latestMessage.Root.ShouldBe(attestation.Data.BeaconBlockRoot);
        }
    }
}
