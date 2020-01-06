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
    public class BeaconChainTestValidity
    {
        public static BeaconState CreateValidBeaconState(IServiceProvider testServiceProvider, ulong? eth1TimestampOverride = null)
        {
            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;

            var beaconChain = testServiceProvider.GetService<BeaconNode.Genesis>();

            var depositCount = miscellaneousParameters.MinimumGenesisActiveValidatorCount;
            (var deposits, _) = TestDeposit.PrepareGenesisDeposits(testServiceProvider, depositCount, gweiValues.MaximumEffectiveBalance, signed: true);
            var eth1BlockHash = new Hash32(Enumerable.Repeat((byte)0x12, 32).ToArray());
            var eth1Timestamp = eth1TimestampOverride ?? miscellaneousParameters.MinimumGenesisTime;
            var state = beaconChain.InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp, deposits);
            return state;
        }

        public static void IsValidGenesisState(IServiceProvider testServiceProvider, BeaconState state, bool valid)
        {
            var beaconChain = testServiceProvider.GetService<BeaconNode.Genesis>();
            var isValid = beaconChain.IsValidGenesisState(state);
            isValid.ShouldBe(valid);
        }

        [TestMethod]
        public void IsValidGenesisStateTrue()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider();

            // Act
            var state = CreateValidBeaconState(testServiceProvider);

            // Assert
            IsValidGenesisState(testServiceProvider, state, true);
        }

        [TestMethod]
        public void IsValidGenesisStateFalseInvalidTimestamp()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider();

            var chainConstants = testServiceProvider.GetService<ChainConstants>();
            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;

            // Act
            var state = CreateValidBeaconState(testServiceProvider, eth1TimestampOverride: (miscellaneousParameters.MinimumGenesisTime - 3 * chainConstants.SecondsPerDay));

            // Assert
            IsValidGenesisState(testServiceProvider, state, false);
        }

        [TestMethod]
        public void IsValidGenesisStateTrueMoreBalance()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider();

            var gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;

            // Act
            var state = CreateValidBeaconState(testServiceProvider);
            state.Validators[0].SetEffectiveBalance(gweiValues.MaximumEffectiveBalance + Gwei.One);

            // Assert
            IsValidGenesisState(testServiceProvider, state, true);
        }

        [TestMethod]
        public void IsValidGenesisStateTrueOneMoreValidator()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider();

            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;

            var beaconChain = testServiceProvider.GetService<BeaconNode.Genesis>();

            var depositCount = miscellaneousParameters.MinimumGenesisActiveValidatorCount + 1;
            (var deposits, _) = TestDeposit.PrepareGenesisDeposits(testServiceProvider, depositCount, gweiValues.MaximumEffectiveBalance, signed: true);
            var eth1BlockHash = new Hash32(Enumerable.Repeat((byte)0x12, 32).ToArray());
            var eth1Timestamp = miscellaneousParameters.MinimumGenesisTime;

            // Act
            var state = beaconChain.InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp, deposits);

            // Assert
            IsValidGenesisState(testServiceProvider, state, true);
        }

        [TestMethod]
        public void IsValidGenesisStateFalseNotEnoughValidators()
        {
            // Arrange
            var testServiceProvider = TestSystem.BuildTestServiceProvider();

            var miscellaneousParameters = testServiceProvider.GetService<IOptions<MiscellaneousParameters>>().Value;
            var gweiValues = testServiceProvider.GetService<IOptions<GweiValues>>().Value;

            var beaconChain = testServiceProvider.GetService<BeaconNode.Genesis>();

            var depositCount = miscellaneousParameters.MinimumGenesisActiveValidatorCount - 1;
            (var deposits, _) = TestDeposit.PrepareGenesisDeposits(testServiceProvider, depositCount, gweiValues.MaximumEffectiveBalance, signed: true);
            var eth1BlockHash = new Hash32(Enumerable.Repeat((byte)0x12, 32).ToArray());
            var eth1Timestamp = miscellaneousParameters.MinimumGenesisTime;

            // Act
            var state = beaconChain.InitializeBeaconStateFromEth1(eth1BlockHash, eth1Timestamp, deposits);

            // Assert
            IsValidGenesisState(testServiceProvider, state, false);
        }
    }
}
