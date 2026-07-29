// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Specs.Test
{
    [TestFixture]
    public class MainnetSpecProviderTests
    {
        private readonly ISpecProvider _specProvider = MainnetSpecProvider.Instance;

        [TestCase(12_243_999ul, false)]
        [TestCase(12_244_000ul, true)]
        public void Berlin_eips(ulong blockNumber, bool isEnabled)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_specProvider.GetSpec((ForkActivation)blockNumber).IsEip2537Enabled, Is.EqualTo(false));
                Assert.That(_specProvider.GetSpec((ForkActivation)blockNumber).IsEip2565Enabled, Is.EqualTo(isEnabled));
                Assert.That(_specProvider.GetSpec((ForkActivation)blockNumber).IsEip2929Enabled, Is.EqualTo(isEnabled));
                Assert.That(_specProvider.GetSpec((ForkActivation)blockNumber).IsEip2930Enabled, Is.EqualTo(isEnabled));
            }
        }

        [TestCase(12_964_999ul, false)]
        [TestCase(12_965_000ul, true)]
        public void London_eips(ulong blockNumber, bool isEnabled)
        {
            if (isEnabled)
                Assert.That(_specProvider.GetSpec((ForkActivation)blockNumber).DifficultyBombDelay, Is.EqualTo(London.Instance.DifficultyBombDelay));
            else
                Assert.That(_specProvider.GetSpec((ForkActivation)blockNumber).DifficultyBombDelay, Is.EqualTo(Berlin.Instance.DifficultyBombDelay));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(_specProvider.GetSpec((ForkActivation)blockNumber).IsEip1559Enabled, Is.EqualTo(isEnabled));
                Assert.That(_specProvider.GetSpec((ForkActivation)blockNumber).IsEip3198Enabled, Is.EqualTo(isEnabled));
                Assert.That(_specProvider.GetSpec((ForkActivation)blockNumber).IsEip3529Enabled, Is.EqualTo(isEnabled));
                Assert.That(_specProvider.GetSpec((ForkActivation)blockNumber).IsEip3541Enabled, Is.EqualTo(isEnabled));
            }
        }

        [TestCase(MainnetSpecProvider.ParisBlockNumber, MainnetSpecProvider.ShanghaiBlockTimestamp, false)]
        [TestCase(MainnetSpecProvider.ParisBlockNumber, MainnetSpecProvider.CancunBlockTimestamp, true)]
        public void Cancun_eips(ulong blockNumber, ulong timestamp, bool isEnabled)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).IsEip1153Enabled, Is.EqualTo(isEnabled));
                Assert.That(_specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).IsEip4844Enabled, Is.EqualTo(isEnabled));
                Assert.That(_specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).IsEip5656Enabled, Is.EqualTo(isEnabled));
                Assert.That(_specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).IsEip4788Enabled, Is.EqualTo(isEnabled));
            }
            if (isEnabled)
            {
                Assert.That(_specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).Eip4788ContractAddress, Is.Not.Null);
            }
            else
            {
                Assert.That(_specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).Eip4788ContractAddress, Is.Null);
            }
        }

        [TestCase(MainnetSpecProvider.ParisBlockNumber, MainnetSpecProvider.CancunBlockTimestamp, false)]
        [TestCase(MainnetSpecProvider.ParisBlockNumber, MainnetSpecProvider.PragueBlockTimestamp, true)]
        public void Prague_eips(ulong blockNumber, ulong timestamp, bool isEnabled)
        {
            Assert.That(_specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).IsEip2935Enabled, Is.EqualTo(isEnabled));
            if (isEnabled)
            {
                Assert.That(_specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).Eip2935ContractAddress, Is.Not.Null);
            }
            else
            {
                Assert.That(_specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).Eip2935ContractAddress, Is.Null);
            }
        }

        [TestCase(MainnetSpecProvider.ParisBlockNumber, MainnetSpecProvider.PragueBlockTimestamp, false)]
        [TestCase(MainnetSpecProvider.ParisBlockNumber, MainnetSpecProvider.OsakaBlockTimestamp, true)]
        public void Osaka_eips(ulong blockNumber, ulong timestamp, bool isEnabled)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(_specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).IsEip7594Enabled, Is.EqualTo(isEnabled));
                Assert.That(_specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).IsEip7823Enabled, Is.EqualTo(isEnabled));
                Assert.That(_specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).IsEip7825Enabled, Is.EqualTo(isEnabled));
                Assert.That(_specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).IsEip7883Enabled, Is.EqualTo(isEnabled));
                Assert.That(_specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).IsEip7918Enabled, Is.EqualTo(isEnabled));
                Assert.That(_specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).IsEip7934Enabled, Is.EqualTo(isEnabled));
                Assert.That(_specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).IsEip7939Enabled, Is.EqualTo(isEnabled));
                Assert.That(_specProvider.GetSpec(new ForkActivation(blockNumber, timestamp)).IsEip7951Enabled, Is.EqualTo(isEnabled));
            }
        }

        [Test]
        public void Dao_block_number_is_correct() => Assert.That(_specProvider.DaoBlockNumber, Is.EqualTo(1920000UL));
    }
}
