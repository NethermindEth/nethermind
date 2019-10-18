using System.Threading.Tasks;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using Shouldly;

namespace Cortex.BeaconNode.Tests
{
    [TestClass]
    public class BeaconChainTest
    {
        //[TestMethod]
        //public void InitialDefaultGenesisTimeShouldBeCorrect()
        //{
        //    // Arrange
        //    var beaconChain = new BeaconChain();

        //    // Act
        //    ulong time = beaconChain.State.GenesisTime;

        //    // Assert
        //    // TODO: Right now this doesn't do much, but will do some proper testing once initialiation is built
        //    var expectedInitialGenesisTime = (ulong)106185600;
        //    time.ShouldBe(expectedInitialGenesisTime);
        //}

        [TestMethod]
        public async Task GenesisWithEmptyParametersTimeShouldReject()
        {
            // Arrange
            TestData.GetMinimalConfiguration(
                out var chainConstants,
                out var miscellaneousParameterOptions,
                out var gweiValueOptions,
                out var initialValueOptions,
                out var timeParameterOptions,
                out var stateListLengthOptions,
                out var maxOperationsPerBlockOptions);
            miscellaneousParameterOptions.CurrentValue.MinimumGenesisActiveValidatorCount = 2;

            var cryptographyService = new CryptographyService();
            var beaconChainUtility = new BeaconChainUtility(cryptographyService, miscellaneousParameterOptions, timeParameterOptions);

            var beaconChain = new BeaconChain(Substitute.For<ILogger<BeaconChain>>(), cryptographyService, beaconChainUtility,
                chainConstants, miscellaneousParameterOptions, gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, maxOperationsPerBlockOptions);

            // Act
            var eth1BlockHash = new Hash32();
            var eth1Timestamp = (ulong)106185600; // 1973-05-14
            var deposits = new Deposit[] { };
            var success = await beaconChain.TryGenesisAsync(eth1BlockHash, eth1Timestamp, deposits);

            // Assert
            success.ShouldBeFalse();
            beaconChain.State.ShouldBeNull();
        }
    }
}
