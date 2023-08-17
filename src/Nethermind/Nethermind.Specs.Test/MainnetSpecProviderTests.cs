// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Specs.Test
{
    [TestFixture]
    public class MainnetSpecProviderTests
    {
        private readonly ISpecProvider _specProvider = MainnetSpecProvider.Instance;

        [TestCase(12_243_999, false)]
        [TestCase(12_244_000, true)]
        public void Berlin_eips(long blockNumber, bool isEnabled)
        {
            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip2315Enabled.Should().Be(false);
            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip2537Enabled.Should().Be(false);
            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip2565Enabled.Should().Be(isEnabled);
            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip2929Enabled.Should().Be(isEnabled);
            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip2930Enabled.Should().Be(isEnabled);
        }

        [TestCase(12_964_999, false)]
        [TestCase(12_965_000, true)]
        public void London_eips(long blockNumber, bool isEnabled)
        {
            if (isEnabled)
                _specProvider.GetSpec((ForkActivation)blockNumber).DifficultyBombDelay.Should().Be(London.Instance.DifficultyBombDelay);
            else
                _specProvider.GetSpec((ForkActivation)blockNumber).DifficultyBombDelay.Should().Be(Berlin.Instance.DifficultyBombDelay);

            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip1559Enabled.Should().Be(isEnabled);
            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip3198Enabled.Should().Be(isEnabled);
            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip3529Enabled.Should().Be(isEnabled);
            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip3541Enabled.Should().Be(isEnabled);
        }

        [TestCase(MainnetSpecProvider.GrayGlacierBlockNumber, MainnetSpecProvider.ShanghaiBlockTimestamp, false)]
        [TestCase(MainnetSpecProvider.GrayGlacierBlockNumber, MainnetSpecProvider.CancunBlockTimestamp, true)]
        public void Cancun_eips(long blockNumber, ulong timestamp, bool isEnabled)
        {
            _specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).IsEip1153Enabled.Should().Be(isEnabled);
            _specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).IsEip4844Enabled.Should().Be(isEnabled);
            _specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).IsEip5656Enabled.Should().Be(isEnabled);
            _specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).IsEip4788Enabled.Should().Be(isEnabled);
            _specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).Eip4788ContractAddress.Should().NotBeNull();
        }

        [Test]
        public void Dao_block_number_is_correct()
        {
            _specProvider.DaoBlockNumber.Should().Be(1920000L);
        }
    }
}
