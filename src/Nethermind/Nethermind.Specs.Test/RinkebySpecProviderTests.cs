// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Specs.Test
{
    [TestFixture]
    public class RinkebySpecProviderTests
    {
        private readonly ISpecProvider _specProvider = RinkebySpecProvider.Instance;

        [TestCase(8_290_927, false)]
        [TestCase(8_290_928, true)]
        public void Berlin_eips(long blockNumber, bool isEnabled)
        {
            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip2315Enabled.Should().Be(false);
            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip2537Enabled.Should().Be(false);
            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip2565Enabled.Should().Be(isEnabled);
            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip2929Enabled.Should().Be(isEnabled);
            _specProvider.GetSpec((ForkActivation)blockNumber).IsEip2930Enabled.Should().Be(isEnabled);
        }

        [TestCase(8_897_987, false)]
        [TestCase(8_897_988, true)]
        public void London_eips(long blockNumber, bool isEnabled)
        {
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
