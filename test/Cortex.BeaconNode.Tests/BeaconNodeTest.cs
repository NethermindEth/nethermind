using Microsoft.VisualStudio.TestTools.UnitTesting;
using Cortex.BeaconNode;
using Shouldly;

namespace Cortex.BeaconNode.Tests
{
    [TestClass]
    public class BeaconChainTest
    {
        [TestMethod]
        public void InitialDefaultGenesisTimeShouldBeCorrect()
        {
            // Arrange
            var beaconChain = new BeaconChain();

            // Act
            ulong time = beaconChain.State.GenesisTime;

            // Assert
            // TODO: Right now this doesn't do much, but will do some proper testing once initialiation is built
            var expectedInitialGenesisTime = (ulong)106185600;
            time.ShouldBe(expectedInitialGenesisTime);
        }
    }
}
