using System.Threading.Tasks;
using Cortex.BeaconNode.Configuration;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
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
                out var miscellaneousParameters,
                out var gweiValues,
                out var initalValues,
                out var timeParameters,
                out var stateListLengths,
                out var maxOperationsPerBlock);
            miscellaneousParameters.MinimumGenesisActiveValidatorCount = 2;

            var cryptographyService = new CryptographyService();
            var beaconChainUtility = new BeaconChainUtility(cryptographyService);

            var beaconChain = new BeaconChain(Substitute.For<ILogger<BeaconChain>>(), cryptographyService, beaconChainUtility,
                chainConstants, miscellaneousParameters, gweiValues, initalValues, timeParameters, stateListLengths, maxOperationsPerBlock);

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
