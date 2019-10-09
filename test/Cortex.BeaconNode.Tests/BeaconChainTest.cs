using System.Threading.Tasks;
using Cortex.Containers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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
            var beaconChainParameters = new BeaconChainParameters()
            {
                MinGenesisActiveValidatorCount = 2,
                MinGenesisTime = 1578009600 // Jan 3, 2020
            };
            var initalValues = new InitialValues()
            {
                GenesisEpoch = 0
            };
            var timeParameters = new TimeParameters();
            var maxOperationsPerBlock = new MaxOperationsPerBlock()
            { 
                MaxDeposits = 16
            };

            var beaconChain = new BeaconChain(null, beaconChainParameters, initalValues, timeParameters, maxOperationsPerBlock);

            // Act
            var eth1BlockHash = new byte[] { };
            var eth1Timestamp = (ulong)106185600; // 1973-05-14
            var deposits = new Deposit[] { };
            var success = await beaconChain.TryGenesisAsync(eth1BlockHash, eth1Timestamp, deposits);

            // Assert
            success.ShouldBeFalse();
            beaconChain.State.ShouldBeNull();
        }

    }
}
