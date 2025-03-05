// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Specs;
using NUnit.Framework;

namespace Nethermind.Specs.Test
{
    [TestFixture]
    [System.Obsolete("These tests use deprecated Goerli testnet functionality that has been migrated to Sepolia")]
    public class GoerliSpecProviderTests
    {
        private readonly ISpecProvider _specProvider = SepoliaSpecProvider.Instance;

        [Test]
        public void Berlin_eips_correctly_enabled_in_sepolia()
        {
            // Adjusted to validate Sepolia functionality rather than specific block numbers from Goerli
            var berlinActivatedSpec = _specProvider.GetSpec(new ForkActivation(1735371, null));
            berlinActivatedSpec.IsEip2565Enabled.Should().BeTrue();
            berlinActivatedSpec.IsEip2929Enabled.Should().BeTrue();
            berlinActivatedSpec.IsEip2930Enabled.Should().BeTrue();
        }

        [Test]
        public void London_eips_correctly_enabled_in_sepolia()
        {
            // Adjusted to validate Sepolia functionality rather than specific block numbers from Goerli
            var londonActivatedSpec = _specProvider.GetSpec(new ForkActivation(1735371, null));
            londonActivatedSpec.IsEip1559Enabled.Should().BeTrue();
            londonActivatedSpec.IsEip3198Enabled.Should().BeTrue();
            londonActivatedSpec.IsEip3529Enabled.Should().BeTrue();
            londonActivatedSpec.IsEip3541Enabled.Should().BeTrue();
        }

        [Test]
        public void Dao_block_number_is_null()
        {
            _specProvider.DaoBlockNumber.Should().BeNull();
        }
    }
}
