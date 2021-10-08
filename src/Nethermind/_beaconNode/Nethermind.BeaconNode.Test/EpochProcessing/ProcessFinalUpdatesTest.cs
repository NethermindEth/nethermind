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
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Shouldly;
namespace Nethermind.BeaconNode.Test.EpochProcessing
{
    [TestClass]
    public class ProcessFinalUpdatesTest
    {
        [TestMethod]
        public void Eth1VoteNoReset()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            timeParameters.SlotsPerEth1VotingPeriod.ShouldBeGreaterThan(timeParameters.SlotsPerEpoch);

            // skip ahead to the end of the epoch
            state.SetSlot((Slot)(timeParameters.SlotsPerEpoch - 1UL));

            // add a vote for each skipped slot.
            for (Slot index = Slot.Zero; index < state.Slot + new Slot(1); index += new Slot(1))
            {
                ulong eth1DepositIndex = state.Eth1DepositIndex;
                Root depositRoot = new Root(Enumerable.Repeat((byte)0xaa, 32).ToArray());
                Bytes32 blockHash = new Bytes32(Enumerable.Repeat((byte)0xbb, 32).ToArray());
                Eth1Data eth1Data = new Eth1Data(depositRoot, eth1DepositIndex, blockHash);
                state.AddEth1DataVote(eth1Data);
            }

            // Act
            RunProcessFinalUpdates(testServiceProvider, state);

            // Assert
            state.Eth1DataVotes.Count.ShouldBe((int)(ulong)timeParameters.SlotsPerEpoch);
        }

        [TestMethod]
        public void Eth1VoteReset()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            //  skip ahead to the end of the voting period
            state.SetSlot((Slot)(timeParameters.SlotsPerEth1VotingPeriod - 1UL));

            // add a vote for each skipped slot.
            for (Slot index = Slot.Zero; index < state.Slot + new Slot(1); index += new Slot(1))
            {
                ulong eth1DepositIndex = state.Eth1DepositIndex;
                Root depositRoot = new Root(Enumerable.Repeat((byte)0xaa, 32).ToArray());
                Bytes32 blockHash = new Bytes32(Enumerable.Repeat((byte)0xbb, 32).ToArray());
                Eth1Data eth1Data = new Eth1Data(depositRoot, eth1DepositIndex, blockHash);
                state.AddEth1DataVote(eth1Data);
            }

            // Act
            RunProcessFinalUpdates(testServiceProvider, state);

            // Assert
            state.Eth1DataVotes.Count.ShouldBe(0);
        }

        [TestMethod]
        public void EffectiveBalanceHysteresis()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            //# Prepare state up to the final-updates.
            //# Then overwrite the balances, we only want to focus to be on the hysteresis based changes.
            TestProcessUtility.RunEpochProcessingTo(testServiceProvider, state, TestProcessStep.ProcessFinalUpdates);

            GweiValues gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;

            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            BeaconStateTransition beaconStateTransition = testServiceProvider.GetService<BeaconStateTransition>();

            // Set some edge cases for balances
            Gwei maximum = gweiValues.MaximumEffectiveBalance;
            Gwei minimum = gweiValues.EjectionBalance;
            Gwei increment = gweiValues.EffectiveBalanceIncrement;
            Gwei halfIncrement = increment / 2;

            EffectiveBalanceCase[] testCases = new[] {
                new EffectiveBalanceCase(maximum, maximum, maximum, "as-is"),
                new EffectiveBalanceCase(maximum, (Gwei)(maximum - 1), maximum - increment, "round down, step lower"),
                new EffectiveBalanceCase(maximum, (Gwei)(maximum + 1), maximum, "round down"),
                new EffectiveBalanceCase(maximum, (Gwei)(maximum - increment), maximum - increment, "exactly 1 step lower"),
                new EffectiveBalanceCase(maximum, (Gwei)(maximum - increment - 1), maximum - (increment * 2), "just 1 over 1 step lower"),
                new EffectiveBalanceCase(maximum, (Gwei)(maximum - increment + 1), maximum - increment, "close to 1 step lower"),
                new EffectiveBalanceCase(minimum, (Gwei)(minimum + (halfIncrement * 3)), minimum, "bigger balance, but not high enough"),
                new EffectiveBalanceCase(minimum, (Gwei)(minimum + (halfIncrement * 3) + 1), minimum + increment, "bigger balance, high enough, but small step"),
                new EffectiveBalanceCase(minimum, (Gwei)(minimum + (halfIncrement * 4) - 1), minimum + increment, "bigger balance, high enough, close to double step"),
                new EffectiveBalanceCase(minimum, (Gwei)(minimum + (halfIncrement * 4)), minimum + (increment * 2), "exact two step balance increment"),
                new EffectiveBalanceCase(minimum, (Gwei)(minimum + (halfIncrement * 4) + 1), minimum + (increment * 2), "over two steps, round down"),
            };

            Epoch currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            for (int index = 0; index < testCases.Length; index++)
            {
                Validator validator = state.Validators[index];
                bool isActive = beaconChainUtility.IsActiveValidator(validator, currentEpoch);
                isActive.ShouldBeTrue();

                EffectiveBalanceCase testCase = testCases[index];
                validator.SetEffectiveBalance(testCase.PreEffective);
                ValidatorIndex validatorIndex = new ValidatorIndex((ulong)index);
                state.SetBalance(validatorIndex, testCase.Balance);
            }

            // Act
            beaconStateTransition.ProcessFinalUpdates(state);

            // Assert
            for (int index = 0; index < testCases.Length; index++)
            {
                EffectiveBalanceCase testCase = testCases[index];
                Validator validator = state.Validators[index];
                validator.EffectiveBalance.ShouldBe(testCase.PostEffective, testCase.Name);
            }
        }

        [TestMethod]
        public void HistoricalRootAccumulator()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider();
            BeaconState state = TestState.PrepareTestState(testServiceProvider);

            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            // skip ahead to near the end of the historical roots period (excl block before epoch processing)
            state.SetSlot((Slot)(timeParameters.SlotsPerHistoricalRoot - 1UL));
            int historyLength = state.HistoricalRoots.Count;

            // Act
            RunProcessFinalUpdates(testServiceProvider, state);

            // Assert
            state.HistoricalRoots.Count.ShouldBe(historyLength + 1);
        }

        private void RunProcessFinalUpdates(IServiceProvider testServiceProvider, BeaconState state)
        {
            TestProcessUtility.RunEpochProcessingWith(testServiceProvider, state, TestProcessStep.ProcessFinalUpdates);
        }

        private class EffectiveBalanceCase
        {
            public EffectiveBalanceCase(Gwei preEffective, Gwei balance, Gwei postEffective, string name)
            {
                PreEffective = preEffective;
                Balance = balance;
                PostEffective = postEffective;
                Name = name;
            }

            public Gwei Balance { get; }
            public string Name { get; }
            public Gwei PostEffective { get; }
            public Gwei PreEffective { get; }
        }
    }
}
