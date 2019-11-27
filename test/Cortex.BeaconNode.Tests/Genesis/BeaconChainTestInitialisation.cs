using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using Shouldly;

namespace Cortex.BeaconNode.Tests.Genesis
{
    [TestClass]
    public class BeaconChainTestInitialisation
    {
        [TestMethod]
        public void TestInitializeBeaconStateFromEth1()
        {
            var useBls = true;

            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider(useBls);

            var chainConstants = testServiceProvider.GetService<ChainConstants>();
            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;
            var initialValues = testServiceProvider.GetService<IOptions<InitialValues>>().Value;
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;

            var depositCount = miscellaneousParameters.MinimumGenesisActiveValidatorCount;

            (var deposits, var depositRoot) = TestDeposit.PrepareGenesisDeposits(depositCount, gweiValues.MaximumEffectiveBalance, signed: useBls,
                chainConstants, initialValues, timeParameters,
                testServiceProvider.GetService<BeaconChainUtility>(), testServiceProvider.GetService<BeaconStateAccessor>());

            var eth1BlockHash = new Hash32(Enumerable.Repeat((byte)0x12, 32).ToArray());
            var eth1Timestamp = miscellaneousParameters.MinimumGenesisTime;

            var beaconChain = testServiceProvider.GetService<BeaconChain>();

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
