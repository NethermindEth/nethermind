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

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Test.Helpers;
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
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
            bool useBls = true;

            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider(useBls, useStore: true);

            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            MiscellaneousParameters miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            GweiValues gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;

            int depositCount = miscellaneousParameters.MinimumGenesisActiveValidatorCount;

            IList<DepositData> deposits = TestDeposit.PrepareGenesisDeposits(testServiceProvider, depositCount, gweiValues.MaximumEffectiveBalance, signed: useBls);
            IDepositStore depositStore = testServiceProvider.GetService<IDepositStore>();
            foreach (DepositData depositData in deposits)
            {
                depositStore.Place(depositData);
            }

            Bytes32 eth1BlockHash = new Bytes32(Enumerable.Repeat((byte)0x12, 32).ToArray());
            ulong eth1Timestamp = miscellaneousParameters.MinimumGenesisTime;

            BeaconNode.GenesisChainStart beaconChain = testServiceProvider.GetService<BeaconNode.GenesisChainStart>();

            // Act
            //# initialize beacon_state
            BeaconState state = beaconChain.InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp);

            // Assert
            state.GenesisTime.ShouldBe(eth1Timestamp - eth1Timestamp % timeParameters.MinimumGenesisDelay + 2 * timeParameters.MinimumGenesisDelay);
            state.Validators.Count.ShouldBe(depositCount);
            state.Eth1Data.DepositRoot.ShouldBe(depositStore.DepositData.Root);
            state.Eth1Data.DepositCount.ShouldBe((ulong)depositCount);
            state.Eth1Data.BlockHash.ShouldBe(eth1BlockHash);
        }
    }
}
