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
    public class BeaconChainTestValidity
    {
        public static BeaconState CreateValidBeaconState(IServiceProvider testServiceProvider, ulong? eth1TimestampOverride = null)
        {
            MiscellaneousParameters miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            GweiValues gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;

            BeaconNode.GenesisChainStart beaconChain = testServiceProvider.GetService<BeaconNode.GenesisChainStart>();

            int depositCount = miscellaneousParameters.MinimumGenesisActiveValidatorCount;
            IList<DepositData> deposits = TestDeposit.PrepareGenesisDeposits(testServiceProvider, depositCount, gweiValues.MaximumEffectiveBalance, signed: true);
            IDepositStore depositStore = testServiceProvider.GetService<IDepositStore>();
            foreach (DepositData deposit in deposits)
            {
                depositStore.Place(deposit);
            }
            
            Bytes32 eth1BlockHash = new Bytes32(Enumerable.Repeat((byte)0x12, 32).ToArray());
            ulong eth1Timestamp = eth1TimestampOverride ?? miscellaneousParameters.MinimumGenesisTime;
            BeaconState state = beaconChain.InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp);
            return state;
        }

        public static void IsValidGenesisState(IServiceProvider testServiceProvider, BeaconState state, bool valid)
        {
            BeaconNode.GenesisChainStart beaconChain = testServiceProvider.GetService<BeaconNode.GenesisChainStart>();
            bool isValid = beaconChain.IsValidGenesisState(state);
            isValid.ShouldBe(valid);
        }

        [TestMethod]
        public void IsValidGenesisStateTrue()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);

            // Act
            BeaconState state = CreateValidBeaconState(testServiceProvider);

            // Assert
            IsValidGenesisState(testServiceProvider, state, true);
        }

        [TestMethod]
        public void IsValidGenesisStateFalseInvalidTimestamp()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);

            ChainConstants chainConstants = testServiceProvider.GetService<ChainConstants>();
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            MiscellaneousParameters miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;

            // Act
            BeaconState state = CreateValidBeaconState(testServiceProvider, eth1TimestampOverride: (miscellaneousParameters.MinimumGenesisTime - 3 * timeParameters.MinimumGenesisDelay));

            // Assert
            IsValidGenesisState(testServiceProvider, state, false);
        }

        [TestMethod]
        public void IsValidGenesisStateTrueMoreBalance()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);

            GweiValues gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;

            // Act
            BeaconState state = CreateValidBeaconState(testServiceProvider);
            state.Validators[0].SetEffectiveBalance(gweiValues.MaximumEffectiveBalance + Gwei.One);

            // Assert
            IsValidGenesisState(testServiceProvider, state, true);
        }

        [TestMethod]
        public void IsValidGenesisStateTrueOneMoreValidator()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);

            MiscellaneousParameters miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            GweiValues gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;

            BeaconNode.GenesisChainStart beaconChain = testServiceProvider.GetService<BeaconNode.GenesisChainStart>();

            int depositCount = miscellaneousParameters.MinimumGenesisActiveValidatorCount + 1;
            IList<DepositData> deposits = TestDeposit.PrepareGenesisDeposits(testServiceProvider, depositCount, gweiValues.MaximumEffectiveBalance, signed: true);
            IDepositStore depositStore = testServiceProvider.GetService<IDepositStore>();
            foreach (DepositData deposit in deposits)
            {
                depositStore.Place(deposit);
            }
            
            Bytes32 eth1BlockHash = new Bytes32(Enumerable.Repeat((byte)0x12, 32).ToArray());
            ulong eth1Timestamp = miscellaneousParameters.MinimumGenesisTime;

            // Act
            BeaconState state = beaconChain.InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp);

            // Assert
            IsValidGenesisState(testServiceProvider, state, true);
        }

        [TestMethod]
        public void IsValidGenesisStateFalseNotEnoughValidators()
        {
            // Arrange
            IServiceProvider testServiceProvider = TestSystem.BuildTestServiceProvider(useStore: true);

            MiscellaneousParameters miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            GweiValues gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;

            BeaconNode.GenesisChainStart beaconChain = testServiceProvider.GetService<BeaconNode.GenesisChainStart>();

            int depositCount = miscellaneousParameters.MinimumGenesisActiveValidatorCount - 1;
            IList<DepositData> deposits = TestDeposit.PrepareGenesisDeposits(testServiceProvider, depositCount, gweiValues.MaximumEffectiveBalance, signed: true);
            IDepositStore depositStore = testServiceProvider.GetService<IDepositStore>();
            foreach (DepositData deposit in deposits)
            {
                depositStore.Place(deposit);
            }
            
            Bytes32 eth1BlockHash = new Bytes32(Enumerable.Repeat((byte)0x12, 32).ToArray());
            ulong eth1Timestamp = miscellaneousParameters.MinimumGenesisTime;

            // Act
            BeaconState state = beaconChain.InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp);

            // Assert
            IsValidGenesisState(testServiceProvider, state, false);
        }
    }
}
