using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.BeaconNode.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Tests.Helpers;
using Nethermind.Core2.Types;
using Shouldly;

namespace Nethermind.BeaconNode.Tests.Genesis
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

            var depositCount = miscellaneousParameters.MinimumGenesisActiveValidatorCount;

            (var deposits, var depositRoot) = TestDeposit.PrepareGenesisDeposits(testServiceProvider, depositCount, gweiValues.MaximumEffectiveBalance, signed: useBls);

            var eth1BlockHash = new Hash32(Enumerable.Repeat((byte)0x12, 32).ToArray());
            var eth1Timestamp = miscellaneousParameters.MinimumGenesisTime;

            var beaconChain = testServiceProvider.GetService<BeaconNode.Genesis>();

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
