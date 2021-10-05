//  Copyright (c) 2021 Demerzel Solutions Limited
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
