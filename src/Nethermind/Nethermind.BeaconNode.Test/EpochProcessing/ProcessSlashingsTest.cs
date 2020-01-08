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
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Types;
using Shouldly;
namespace Nethermind.BeaconNode.Test.EpochProcessing
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
