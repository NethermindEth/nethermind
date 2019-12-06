﻿using System;
using System.Linq;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Tests.Helpers;
using Cortex.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shouldly;

namespace Cortex.BeaconNode.Tests.Genesis
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
            state.Validators[0].SetEffectiveBalance(gweiValues.MaximumEffectiveBalance + (Gwei)1);

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
