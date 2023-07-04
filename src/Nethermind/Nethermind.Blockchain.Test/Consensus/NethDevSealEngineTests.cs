// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Consensus
{
    [TestFixture]
    public class NethDevSealEngineTests
    {
        [Test, Timeout(Timeout.MaxTestTime)]
        public void Defaults_are_fine()
        {
            NethDevSealEngine nethDevSealEngine = new();
            nethDevSealEngine.Address.Should().Be(Address.Zero);
            nethDevSealEngine.CanSeal(1, Keccak.Zero).Should().BeTrue();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Can_seal_returns_true()
        {
            NethDevSealEngine nethDevSealEngine = new();
            nethDevSealEngine.CanSeal(1, Keccak.Zero).Should().BeTrue();
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Validations_return_true()
        {
            NethDevSealEngine nethDevSealEngine = new();
            nethDevSealEngine.ValidateParams(null, null).Should().Be(true);
            nethDevSealEngine.ValidateSeal(null, false).Should().Be(true);
            nethDevSealEngine.ValidateSeal(null, true).Should().Be(true);
        }

        [Test, Timeout(Timeout.MaxTestTime)]
        public void Block_sealing_sets_the_hash()
        {
            Block block = Build.A.Block.TestObject;
            block.Header.Hash = Keccak.Zero;

            NethDevSealEngine nethDevSealEngine = new();
            nethDevSealEngine.SealBlock(block, CancellationToken.None);
            block.Hash.Should().NotBe(Keccak.Zero);
        }
    }
}
