// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Specs.Test
{
    [TestFixture]
    public class CustomSpecProviderTests
    {
        [Test]
        public void When_no_transitions_specified_throws_argument_exception()
        {
            Assert.Throws<ArgumentException>(() => _ = new CustomSpecProvider());
        }

        [Test]
        public void When_first_release_is_not_at_block_zero_then_throws_argument_exception()
        {
            Assert.Throws<ArgumentException>(() => _ = new CustomSpecProvider((1, Byzantium.Instance)), "ordered");

            Assert.Throws<ArgumentException>(() => _ = new CustomSpecProvider(
                (1, Byzantium.Instance),
                (0, Frontier.Instance)), "not ordered");
        }

        [Test]
        public void When_only_one_release_is_specified_then_returns_that_release()
        {
            var specProvider = new CustomSpecProvider((0, Byzantium.Instance));
            Assert.IsInstanceOf<Byzantium>(specProvider.GetSpec(0), "0");
            Assert.IsInstanceOf<Byzantium>(specProvider.GetSpec(1), "1");
        }

        [Test]
        public void Can_find_dao_block_number()
        {
            long daoBlockNumber = 100;
            var specProvider = new CustomSpecProvider(
                (0L, Frontier.Instance),
                (daoBlockNumber, Dao.Instance));

            Assert.AreEqual(daoBlockNumber, specProvider.DaoBlockNumber);
        }

        [Test]
        public void If_no_dao_then_no_dao_block_number()
        {
            var specProvider = new CustomSpecProvider(
                (0L, Frontier.Instance),
                (1L, Homestead.Instance));

            Assert.IsNull(specProvider.DaoBlockNumber);
        }

        [Test]
        public void When_more_releases_specified_then_transitions_work()
        {
            var specProvider = new CustomSpecProvider(
                (0, Frontier.Instance),
                (1, Homestead.Instance));
            Assert.IsInstanceOf<Frontier>(specProvider.GetSpec(0), "2 releases, block 0");
            Assert.IsInstanceOf<Homestead>(specProvider.GetSpec(1), "2 releases, block 1");

            specProvider = new CustomSpecProvider(
                (0, Frontier.Instance),
                (1, Homestead.Instance),
                (10, Byzantium.Instance));
            Assert.IsInstanceOf<Frontier>(specProvider.GetSpec(0), "3 releases, block 0");
            Assert.IsInstanceOf<Homestead>(specProvider.GetSpec(1), "3 releases, block 1");
            Assert.IsInstanceOf<Byzantium>(specProvider.GetSpec(100), "3 releases, block 10");
        }
    }
}
