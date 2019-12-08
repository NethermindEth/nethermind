using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Tests.Helpers;
using Nethermind.Core2.Types;
using Shouldly;

namespace Nethermind.BeaconNode.Tests.EpochProcessing
{
    [TestClass]
    public class ProcessSlashingsTest
    {
        [TestMethod]
        public void MaximumPenalties()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            StateListLengths stateListLengths = testServiceProvider.GetService<IOptions<StateListLengths>>().Value;
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            int slashedCount = (state.Validators.Count / 3) + 1;
            Epoch currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            Epoch outEpoch = currentEpoch + new Epoch((ulong)stateListLengths.EpochsPerSlashingsVector / 2);

            var slashedIndices = Enumerable.Range(0, slashedCount).ToList();
            SlashValidators(testServiceProvider, state, slashedIndices, Enumerable.Repeat(outEpoch, slashedCount));

            Gwei totalBalance = beaconStateAccessor.GetTotalActiveBalance(state);
            Gwei totalPenalties = state.Slashings.Aggregate(Gwei.Zero, (accumulator, x) => accumulator + x);

            (totalBalance / 3).ShouldBeLessThanOrEqualTo(totalPenalties);

            // Act
            RunProcessSlashings(testServiceProvider, state);

            // Assert
            foreach (int index in slashedIndices)
            {
                state.Balances[index].ShouldBe(Gwei.Zero, $"Incorrect balance {index}");
            }
        }

        private void RunProcessSlashings(IServiceProvider testServiceProvider, BeaconState state)
        {
            TestProcessUtility.RunEpochProcessingWith(testServiceProvider, state, TestProcessStep.ProcessSlashings);
        }

        private void SlashValidators(IServiceProvider testServiceProvider, BeaconState state, IEnumerable<int> indices, IEnumerable<Epoch> outEpochs)
        {
            StateListLengths stateListLengths = testServiceProvider.GetService<IOptions<StateListLengths>>().Value;

            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            BeaconStateMutator beaconStateMutator = testServiceProvider.GetService<BeaconStateMutator>();

            Gwei totalSlashedBalance = Gwei.Zero;
            var items = indices.Zip(outEpochs, (index, outEpoch) => new { index, outEpoch });
            foreach (var item in items)
            {
                Validator validator = state.Validators[item.index];
                validator.SetSlashed();
                beaconStateMutator.InitiateValidatorExit(state, new ValidatorIndex((ulong)item.index));
                validator.SetWithdrawableEpoch(item.outEpoch);
                totalSlashedBalance += validator.EffectiveBalance;
            }

            Epoch currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            Epoch slashingsIndex = (Epoch)(currentEpoch % stateListLengths.EpochsPerSlashingsVector);
            state.SetSlashings(slashingsIndex, totalSlashedBalance);
        }
    }
}
