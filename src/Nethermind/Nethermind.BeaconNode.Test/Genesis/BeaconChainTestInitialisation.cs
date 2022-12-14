// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
