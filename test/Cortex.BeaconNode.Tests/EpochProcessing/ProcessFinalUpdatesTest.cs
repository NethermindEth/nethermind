using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.BeaconNode.Tests.EpochProcessing
{
    [TestClass]
    public class ProcessFinalUpdatesTest
    {
        [TestMethod]
        public void Eth1VoteNoReset()
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
                out var maxOperationsPerBlockOptions,
                out _);
            (_, _, _, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            var timeParameters = timeParameterOptions.CurrentValue;
            timeParameters.SlotsPerEth1VotingPeriod.ShouldBeGreaterThan(timeParameters.SlotsPerEpoch);

            // skip ahead to the end of the epoch
            state.SetSlot(timeParameters.SlotsPerEpoch - new Slot(1));

            // add a vote for each skipped slot.
            for (var index = Slot.Zero; index < state.Slot + new Slot(1); index += new Slot(1))
            {
                var eth1DepositIndex = state.Eth1DepositIndex;
                var depositRoot = new Hash32(Enumerable.Repeat((byte)0xaa, 32).ToArray());
                var blockHash = new Hash32(Enumerable.Repeat((byte)0xbb, 32).ToArray());
                var eth1Data = new Eth1Data(depositRoot, eth1DepositIndex, blockHash);
                state.AddEth1DataVote(eth1Data);
            }

            // Act
            RunProcessFinalUpdates(beaconStateTransition, timeParameterOptions.CurrentValue, state);

            // Assert
            state.Eth1DataVotes.Count.ShouldBe((int)(ulong)timeParameters.SlotsPerEpoch);
        }

        [TestMethod]
        public void Eth1VoteReset()
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
                out var maxOperationsPerBlockOptions,
                out _);
            (_, _, _, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            var timeParameters = timeParameterOptions.CurrentValue;

            //  skip ahead to the end of the voting period
            state.SetSlot(timeParameters.SlotsPerEth1VotingPeriod - new Slot(1));

            // add a vote for each skipped slot.
            for (var index = Slot.Zero; index < state.Slot + new Slot(1); index += new Slot(1))
            {
                var eth1DepositIndex = state.Eth1DepositIndex;
                var depositRoot = new Hash32(Enumerable.Repeat((byte)0xaa, 32).ToArray());
                var blockHash = new Hash32(Enumerable.Repeat((byte)0xbb, 32).ToArray());
                var eth1Data = new Eth1Data(depositRoot, eth1DepositIndex, blockHash);
                state.AddEth1DataVote(eth1Data);
            }

            // Act
            RunProcessFinalUpdates(beaconStateTransition, timeParameterOptions.CurrentValue, state);

            // Assert
            state.Eth1DataVotes.Count.ShouldBe(0);
        }

        [TestMethod]
        public void EffectiveBalanceHysteresis()
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
                out var maxOperationsPerBlockOptions,
                out _);
            (var beaconChainUtility, var beaconStateAccessor, _, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            //# Prepare state up to the final-updates.
            //# Then overwrite the balances, we only want to focus to be on the hysteresis based changes.
            TestProcessUtility.RunEpochProcessingTo(beaconStateTransition, timeParameterOptions.CurrentValue, state, TestProcessStep.ProcessFinalUpdates);

            // Set some edge cases for balances
            var gweiValues = gweiValueOptions.CurrentValue;
            var maximum = gweiValues.MaximumEffectiveBalance;
            var minimum = gweiValues.EjectionBalance;
            var increment = gweiValues.EffectiveBalanceIncrement;
            var halfIncrement = increment / 2;
            var one = new Gwei(1);

            var testCases = new[] {
                new EffectiveBalanceCase(maximum, maximum, maximum, "as-is"),
                new EffectiveBalanceCase(maximum, maximum - one, maximum - increment, "round down, step lower"),
                new EffectiveBalanceCase(maximum, maximum + one, maximum, "round down"),
                new EffectiveBalanceCase(maximum, maximum - increment, maximum - increment, "exactly 1 step lower"),
                new EffectiveBalanceCase(maximum, maximum - increment - one, maximum - (increment * 2), "just 1 over 1 step lower"),
                new EffectiveBalanceCase(maximum, maximum - increment + one, maximum - increment, "close to 1 step lower"),
                new EffectiveBalanceCase(minimum, minimum + (halfIncrement * 3), minimum, "bigger balance, but not high enough"),
                new EffectiveBalanceCase(minimum, minimum + (halfIncrement * 3) + one, minimum + increment, "bigger balance, high enough, but small step"),
                new EffectiveBalanceCase(minimum, minimum + (halfIncrement * 4) - one, minimum + increment, "bigger balance, high enough, close to double step"),
                new EffectiveBalanceCase(minimum, minimum + (halfIncrement * 4), minimum + (increment * 2), "exact two step balance increment"),
                new EffectiveBalanceCase(minimum, minimum + (halfIncrement * 4) + one, minimum + (increment * 2), "over two steps, round down"),
            };

            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            for (var index = 0; index < testCases.Length; index++)
            {
                var validator = state.Validators[index];
                var isActive = beaconChainUtility.IsActiveValidator(validator, currentEpoch);
                isActive.ShouldBeTrue();

                var testCase = testCases[index];
                validator.SetEffectiveBalance(testCase.PreEffective);
                var validatorIndex = new ValidatorIndex((ulong)index);
                state.SetBalance(validatorIndex, testCase.Balance);
            }

            // Act
            beaconStateTransition.ProcessFinalUpdates(state);

            // Assert
            for (var index = 0; index < testCases.Length; index++)
            {
                var testCase = testCases[index];
                var validator = state.Validators[index];
                validator.EffectiveBalance.ShouldBe(testCase.PostEffective, testCase.Name);
            }
        }

        [TestMethod]
        public void HistoricalRootAccumulator()
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
                out var maxOperationsPerBlockOptions,
                out _);
            (_, _, _, var beaconStateTransition, var state) = TestState.PrepareTestState(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            // skip ahead to near the end of the historical roots period (excl block before epoch processing)
            state.SetSlot(timeParameterOptions.CurrentValue.SlotsPerHistoricalRoot - new Slot(1));
            var historyLength = state.HistoricalRoots.Count;

            // Act
            RunProcessFinalUpdates(beaconStateTransition, timeParameterOptions.CurrentValue, state);

            // Assert
            state.HistoricalRoots.Count.ShouldBe(historyLength + 1);
        }

        private void RunProcessFinalUpdates(BeaconStateTransition beaconStateTransition, TimeParameters timeParameters, BeaconState state)
        {
            TestProcessUtility.RunEpochProcessingWith(beaconStateTransition, timeParameters, state, TestProcessStep.ProcessFinalUpdates);
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
