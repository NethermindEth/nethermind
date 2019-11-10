using System.Linq;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
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
            TestConfiguration.GetMinimalConfiguration(
                out var chainConstants,
                out var miscellaneousParameterOptions,
                out var gweiValueOptions,
                out var initialValueOptions,
                out var timeParameterOptions,
                out var stateListLengthOptions,
                out var rewardsAndPenaltiesOptions,
                out var maxOperationsPerBlockOptions);

            var loggerFactory = new LoggerFactory(new[] {
                new ConsoleLoggerProvider(TestOptionsMonitor.Create(new ConsoleLoggerOptions()))
            });

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
            var beaconChainUtility = new BeaconChainUtility(loggerFactory.CreateLogger<BeaconChainUtility>(),
                miscellaneousParameterOptions, gweiValueOptions, timeParameterOptions, 
                cryptographyService);
            var beaconStateAccessor = new BeaconStateAccessor(miscellaneousParameterOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions,
                cryptographyService, beaconChainUtility);
            var beaconStateMutator = new BeaconStateMutator(chainConstants, timeParameterOptions, stateListLengthOptions, rewardsAndPenaltiesOptions,
                 beaconChainUtility, beaconStateAccessor);

            var depositCount = miscellaneousParameterOptions.CurrentValue.MinimumGenesisActiveValidatorCount;
            (var deposits, var depositRoot) = TestData.PrepareGenesisDeposits(chainConstants, initialValueOptions.CurrentValue, timeParameterOptions.CurrentValue, beaconChainUtility, depositCount, gweiValueOptions.CurrentValue.MaximumEffectiveBalance, signed: useBls);
            var eth1BlockHash = new Hash32(Enumerable.Repeat((byte)0x12, 32).ToArray());
            var eth1Timestamp = miscellaneousParameterOptions.CurrentValue.MinimumGenesisTime;

            var beaconChain = new BeaconChain(loggerFactory.CreateLogger<BeaconChain>(), chainConstants, miscellaneousParameterOptions,
                gweiValueOptions, initialValueOptions, timeParameterOptions, stateListLengthOptions, maxOperationsPerBlockOptions,
                cryptographyService, beaconChainUtility, beaconStateAccessor, beaconStateMutator);

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
