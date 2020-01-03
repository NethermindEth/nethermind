//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Containers;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2.Types;
using Shouldly;

namespace Nethermind.BeaconNode.Test.Genesis
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
