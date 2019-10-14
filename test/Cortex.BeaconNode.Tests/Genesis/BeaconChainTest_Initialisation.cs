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
        private static readonly Gwei MAX_EFFECTIVE_BALANCE = new Gwei(32000000000);

        [TestMethod]
        public void TestInitializeBeaconStateFromEth1()
        {
            // Arrange
            var useBls = true;
            var beaconChainParameters = new BeaconChainParameters()
            {
                MinGenesisActiveValidatorCount = 64,
                MinGenesisTime = 1578009600 // Jan 3, 2020
            };
            var initalValues = new InitialValues()
            {
                GenesisEpoch = new Epoch(0)
            };
            var timeParameters = new TimeParameters();
            var maxOperationsPerBlock = new MaxOperationsPerBlock()
            {
                MaxDeposits = 16
            };

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

            var depositCount = beaconChainParameters.MinGenesisActiveValidatorCount;
            (var deposits, var depositRoot) = TestData.PrepareGenesisDeposits(beaconChainUtility, depositCount, MAX_EFFECTIVE_BALANCE, signed: useBls);
            var eth1BlockHash = new Hash32(Enumerable.Repeat((byte)0x12, 32).ToArray());
            var eth1Timestamp = beaconChainParameters.MinGenesisTime;

            var beaconChain = new BeaconChain(testLogger, cryptographyService, beaconChainUtility, beaconChainParameters, initalValues, timeParameters, maxOperationsPerBlock);

            // Act
            //# initialize beacon_state
            var state = beaconChain.InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp, deposits);

            // Assert
            state.GenesisTime.ShouldBe(eth1Timestamp - eth1Timestamp % timeParameters.SecondsPerDay + 2 * timeParameters.SecondsPerDay);
            state.Validators.Count.ShouldBe(depositCount);
            state.Eth1Data.DepositRoot.ShouldBe(depositRoot);
            state.Eth1Data.DepositCount.ShouldBe((ulong)depositCount);
            state.Eth1Data.BlockHash.ShouldBe(eth1BlockHash);
        }
    }
}
