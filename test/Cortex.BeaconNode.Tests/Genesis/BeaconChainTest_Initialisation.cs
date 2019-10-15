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
    public class BeaconChainTest_Initialisation
    {
        [TestMethod]
        public void TestInitializeBeaconStateFromEth1()
        {
            var useBls = true;

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

            ICryptographyService cryptographyService;
            if (useBls)
            {
                cryptographyService = new CryptographyService();
            }
            else
            {
                cryptographyService = Substitute.For<ICryptographyService>();
                cryptographyService
                    .BlsVerify(Arg.Any<BlsPublicKey>(), Arg.Any<Hash32>(), Arg.Any<BlsSignature>(), Arg.Any<Domain>())
                    .Returns(true);
                cryptographyService
                    .Hash(Arg.Any<Hash32>(), Arg.Any<Hash32>())
                    .Returns(callInfo =>
                    {
                        return new Hash32(TestUtility.Hash(callInfo.ArgAt<Hash32>(0).AsSpan(), callInfo.ArgAt<Hash32>(1).AsSpan()));
                    });
            }
            var beaconChainUtility = new BeaconChainUtility(cryptographyService);

            var depositCount = miscellaneousParameters.MinimumGenesisActiveValidatorCount;
            (var deposits, var depositRoot) = TestData.PrepareGenesisDeposits(chainConstants, timeParameters, beaconChainUtility, depositCount, gweiValues.MaximumEffectiveBalance, signed: useBls);
            var eth1BlockHash = new Hash32(Enumerable.Repeat((byte)0x12, 32).ToArray());
            var eth1Timestamp = miscellaneousParameters.MinimumGenesisTime;

            var beaconChain = new BeaconChain(testLogger, cryptographyService, beaconChainUtility, 
                chainConstants, miscellaneousParameters, gweiValues, initalValues, timeParameters, 
                stateListLengths, maxOperationsPerBlock);

            // Act
            //# initialize beacon_state
            var state = beaconChain.InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp, deposits);

            // Assert
            state.GenesisTime.ShouldBe(eth1Timestamp - eth1Timestamp % chainConstants.SecondsPerDay + 2 * chainConstants.SecondsPerDay);
            state.Validators.Count.ShouldBe(depositCount);
            state.Eth1Data.DepositRoot.ShouldBe(depositRoot);
            state.Eth1Data.DepositCount.ShouldBe((ulong)depositCount);
            state.Eth1Data.BlockHash.ShouldBe(eth1BlockHash);
        }
    }
}
