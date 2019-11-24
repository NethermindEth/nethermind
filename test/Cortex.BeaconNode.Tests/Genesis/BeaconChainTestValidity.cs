using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.BeaconNode.Tests.Genesis
{
    [TestClass]
    public class BeaconChainTestValidity
    {
        public static BeaconState CreateValidBeaconState(BeaconChain beaconChain, BeaconChainUtility beaconChainUtility, BeaconStateAccessor beaconStateAccessor,
            ChainConstants chainConstants, InitialValues initialValues, MiscellaneousParameters miscellaneousParameters, GweiValues gweiValues,
            TimeParameters timeParameters, ulong? eth1TimestampOverride = null)
        {
            var depositCount = miscellaneousParameters.MinimumGenesisActiveValidatorCount;
            (var deposits, _) = TestDeposit.PrepareGenesisDeposits(depositCount, gweiValues.MaximumEffectiveBalance, signed: true,
                chainConstants, initialValues, timeParameters, 
                beaconChainUtility, beaconStateAccessor);
            var eth1BlockHash = new Hash32(Enumerable.Repeat((byte)0x12, 32).ToArray());
            var eth1Timestamp = eth1TimestampOverride ?? miscellaneousParameters.MinimumGenesisTime;
            var state = beaconChain.InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp, deposits);
            return state;
        }

        public static void IsValidGenesisState(BeaconChain beaconChain, BeaconState state, bool valid)
        {
            var isValid = beaconChain.IsValidGenesisState(state);
            isValid.ShouldBe(valid);
        }

        [TestMethod]
        public void IsValidGenesisStateTrue()
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
            (var beaconChainUtility, var beaconStateAccessor, var beaconChain) = PrepareComponents(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            // Act
            var state = CreateValidBeaconState(beaconChain, beaconChainUtility, beaconStateAccessor, chainConstants, initialValueOptions.CurrentValue, miscellaneousParameterOptions.CurrentValue, gweiValueOptions.CurrentValue, timeParameterOptions.CurrentValue);

            // Assert
            IsValidGenesisState(beaconChain, state, true);
        }

        [TestMethod]
        public void IsValidGenesisStateFalseInvalidTimestamp()
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
            (var beaconChainUtility, var beaconStateAccessor, var beaconChain) = PrepareComponents(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            // Act
            var state = CreateValidBeaconState(beaconChain, beaconChainUtility, beaconStateAccessor, chainConstants, initialValueOptions.CurrentValue, miscellaneousParameterOptions.CurrentValue,
                gweiValueOptions.CurrentValue, timeParameterOptions.CurrentValue, eth1TimestampOverride: (miscellaneousParameterOptions.CurrentValue.MinimumGenesisTime - 3 * chainConstants.SecondsPerDay));

            // Assert
            IsValidGenesisState(beaconChain, state, false);
        }

        [TestMethod]
        public void IsValidGenesisStateTrueMoreBalance()
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
            (var beaconChainUtility, var beaconStateAccessor, var beaconChain) = PrepareComponents(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            // Act
            var state = CreateValidBeaconState(beaconChain, beaconChainUtility, beaconStateAccessor, chainConstants, initialValueOptions.CurrentValue, miscellaneousParameterOptions.CurrentValue,
                gweiValueOptions.CurrentValue, timeParameterOptions.CurrentValue);
            state.Validators[0].SetEffectiveBalance(gweiValueOptions.CurrentValue.MaximumEffectiveBalance + (Gwei)1);

            // Assert
            IsValidGenesisState(beaconChain, state, true);
        }

        [TestMethod]
        public void IsValidGenesisStateTrueOneMoreValidator()
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
            (var beaconChainUtility, var beaconStateAccessor, var beaconChain) = PrepareComponents(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            var depositCount = miscellaneousParameterOptions.CurrentValue.MinimumGenesisActiveValidatorCount + 1;
            (var deposits, _) = TestDeposit.PrepareGenesisDeposits(depositCount, gweiValueOptions.CurrentValue.MaximumEffectiveBalance, signed: true,
                chainConstants, initialValueOptions.CurrentValue, timeParameterOptions.CurrentValue,
                beaconChainUtility, beaconStateAccessor);
            var eth1BlockHash = new Hash32(Enumerable.Repeat((byte)0x12, 32).ToArray());
            var eth1Timestamp = miscellaneousParameterOptions.CurrentValue.MinimumGenesisTime;

            // Act
            var state = beaconChain.InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp, deposits);

            // Assert
            IsValidGenesisState(beaconChain, state, true);
        }

        [TestMethod]
        public void IsValidGenesisStateFalseNotEnoughValidators()
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
            (var beaconChainUtility, var beaconStateAccessor, var beaconChain) = PrepareComponents(chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions);

            var depositCount = miscellaneousParameterOptions.CurrentValue.MinimumGenesisActiveValidatorCount - 1;
            (var deposits, _) = TestDeposit.PrepareGenesisDeposits(depositCount, gweiValueOptions.CurrentValue.MaximumEffectiveBalance, signed: true,
                chainConstants, initialValueOptions.CurrentValue, timeParameterOptions.CurrentValue, 
                beaconChainUtility, beaconStateAccessor);
            var eth1BlockHash = new Hash32(Enumerable.Repeat((byte)0x12, 32).ToArray());
            var eth1Timestamp = miscellaneousParameterOptions.CurrentValue.MinimumGenesisTime;

            // Act
            var state = beaconChain.InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp, deposits);

            // Assert
            IsValidGenesisState(beaconChain, state, false);
        }

        private static (BeaconChainUtility, BeaconStateAccessor, BeaconChain) PrepareComponents(ChainConstants chainConstants,
            IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<GweiValues> gweiValueOptions,
            IOptionsMonitor<InitialValues> initialValueOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<StateListLengths> stateListLengthOptions,
            IOptionsMonitor<RewardsAndPenalties> rewardsAndPenaltiesOptions,
            IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions)
        {
            var loggerFactory = new LoggerFactory(new[] {
                new ConsoleLoggerProvider(TestOptionsMonitor.Create(new ConsoleLoggerOptions()))
            });

            var cryptographyService = new CryptographyService();
            var beaconChainUtility = new BeaconChainUtility(loggerFactory.CreateLogger<BeaconChainUtility>(),
                miscellaneousParameterOptions, gweiValueOptions, timeParameterOptions,
                cryptographyService);
            var beaconStateAccessor = new BeaconStateAccessor(miscellaneousParameterOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions,
                cryptographyService, beaconChainUtility);
            var beaconStateMutator = new BeaconStateMutator(chainConstants, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions,
                 beaconChainUtility, beaconStateAccessor);
            var beaconStateTransition = new BeaconStateTransition(loggerFactory.CreateLogger<BeaconStateTransition>(),
                chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions, maxOperationsPerBlockOptions,
                cryptographyService, beaconChainUtility, beaconStateAccessor, beaconStateMutator);

            var beaconChain = new BeaconChain(loggerFactory.CreateLogger<BeaconChain>(), 
                chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, maxOperationsPerBlockOptions,
                cryptographyService, beaconChainUtility, beaconStateAccessor, beaconStateMutator, beaconStateTransition);

            return (beaconChainUtility, beaconStateAccessor, beaconChain);
        }
    }
}
