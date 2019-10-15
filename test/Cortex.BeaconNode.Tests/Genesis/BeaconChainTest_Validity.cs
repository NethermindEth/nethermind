using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using Shouldly;

namespace Cortex.BeaconNode.Tests.Genesis
{
    [TestClass]
    public class BeaconChainTest_Validity
    {
        public BeaconState CreateValidBeaconState(BeaconChain beaconChain, BeaconChainUtility beaconChainUtility,
            ChainConstants chainConstants, MiscellaneousParameters miscellaneousParameters, GweiValues gweiValues,
            TimeParameters timeParameters, ulong? eth1TimestampOverride = null)
        {
            var depositCount = miscellaneousParameters.MinimumGenesisActiveValidatorCount;
            (var deposits, _) = TestData.PrepareGenesisDeposits(chainConstants, timeParameters, beaconChainUtility, depositCount, gweiValues.MaximumEffectiveBalance, signed: true);
            var eth1BlockHash = new Hash32(Enumerable.Repeat((byte)0x12, 32).ToArray());
            var eth1Timestamp = eth1TimestampOverride ?? miscellaneousParameters.MinimumGenesisTime;
            var state = beaconChain.InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp, deposits);
            return state;
        }

        public void IsValidGenesisState(BeaconChain beaconChain, BeaconState state, bool valid)
        {
            var isValid = beaconChain.IsValidGenesisState(state);
            isValid.ShouldBe(valid);
        }

        [TestMethod]
        public void IsValidGenesisStateTrue()
        {
            // Arrange
            TestData.GetMinimalConfiguration(
                out var chainConstants,
                out var miscellaneousParameters,
                out var gweiValues,
                out var initalValues,
                out var timeParameters,
                out var stateListLengths,
                out var maxOperationsPerBlock);

            var testLogger = Substitute.For<ILogger<BeaconChain>>();

            var cryptographyService = new CryptographyService();
            var beaconChainUtility = new BeaconChainUtility(cryptographyService);

            var beaconChain = new BeaconChain(testLogger, cryptographyService, beaconChainUtility,
                chainConstants, miscellaneousParameters, gweiValues, initalValues, timeParameters,
                stateListLengths, maxOperationsPerBlock);

            // Act
            var state = CreateValidBeaconState(beaconChain, beaconChainUtility, chainConstants, miscellaneousParameters, gweiValues, timeParameters);

            // Assert
            IsValidGenesisState(beaconChain, state, true);
        }

        [TestMethod]
        public void IsValidGenesisStateFalseInvalidTimestamp()
        {
            // Arrange
            TestData.GetMinimalConfiguration(
                out var chainConstants,
                out var miscellaneousParameters,
                out var gweiValues,
                out var initalValues,
                out var timeParameters,
                out var stateListLengths,
                out var maxOperationsPerBlock);

            var testLogger = Substitute.For<ILogger<BeaconChain>>();

            var cryptographyService = new CryptographyService();
            var beaconChainUtility = new BeaconChainUtility(cryptographyService);

            var beaconChain = new BeaconChain(testLogger, cryptographyService, beaconChainUtility,
                chainConstants, miscellaneousParameters, gweiValues, initalValues, timeParameters,
                stateListLengths, maxOperationsPerBlock);

            // Act
            var state = CreateValidBeaconState(beaconChain, beaconChainUtility, chainConstants, miscellaneousParameters,
                gweiValues, timeParameters, eth1TimestampOverride:(miscellaneousParameters.MinimumGenesisTime - 3 * chainConstants.SecondsPerDay));

            // Assert
            IsValidGenesisState(beaconChain, state, false);
        }

    }
}
