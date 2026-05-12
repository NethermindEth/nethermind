// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Consensus
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class NethDevSealEngineTests
    {
        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Defaults_are_fine()
        {
            NethDevSealEngine nethDevSealEngine = new();
            Assert.That(nethDevSealEngine.Address, Is.EqualTo(Address.Zero));
            Assert.That(nethDevSealEngine.CanSeal(1, Keccak.Zero), Is.True);
        }

        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Can_seal_returns_true()
        {
            NethDevSealEngine nethDevSealEngine = new();
            Assert.That(nethDevSealEngine.CanSeal(1, Keccak.Zero), Is.True);
        }

        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Validations_return_true()
        {
            NethDevSealEngine nethDevSealEngine = new();
            Assert.That(nethDevSealEngine.ValidateParams(null, null), Is.EqualTo(true));
            Assert.That(nethDevSealEngine.ValidateSeal(null, false), Is.EqualTo(true));
            Assert.That(nethDevSealEngine.ValidateSeal(null, true), Is.EqualTo(true));
        }

        [Test, MaxTime(Timeout.MaxTestTime)]
        public void Block_sealing_sets_the_hash()
        {
            Block block = Build.A.Block.TestObject;
            block.Header.Hash = Keccak.Zero;

            NethDevSealEngine nethDevSealEngine = new();
            nethDevSealEngine.SealBlock(block, CancellationToken.None);
            Assert.That(block.Hash, Is.Not.EqualTo(Keccak.Zero));
        }
    }
}
