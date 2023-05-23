// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Specs.Test
{
    [TestFixture]
    public class RopstenSpecProviderTests
    {
        private readonly ISpecProvider _specProvider = RopstenSpecProvider.Instance;

        [TestCase(9_812_188, false)]
        [TestCase(9_812_189, true)]
        public void Berlin_eips(long blockNumber, bool isEnabled)
        {
            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip2315Enabled.Should().Be(false);
            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip2537Enabled.Should().Be(false);
            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip2565Enabled.Should().Be(isEnabled);
            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip2929Enabled.Should().Be(isEnabled);
            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip2930Enabled.Should().Be(isEnabled);
        }

        [TestCase(10_499_400, false)]
        [TestCase(10_499_401, true)]
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

        [Test]
        public void Dao_block_number_is_null()
        {
            _specProvider.DaoBlockNumber.Should().BeNull();
        }
    }
}
