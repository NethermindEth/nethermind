/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test
{
    [TestFixture]
    public class LruCacheTests
    {
        private const int Capacity = 16;

        private readonly Account[] _accounts = new Account[Capacity];
        private readonly Address[] _addresses = new Address[Capacity];

        [SetUp]
        public void Setup()
        {
            for (int i = 0; i < Capacity; i++)
            {
                _accounts[i] = Build.An.Account.TestObject;
                _addresses[i] = Build.An.Address.FromNumber(i).TestObject;
            }
        }

        [Test]
        public void Beyond_capacity()
        {
            LruCache<Address, Account> cache = new LruCache<Address, Account>(Capacity);
            for (int i = 0; i < Capacity; i++)
            {
                cache.Set(_addresses[i], _accounts[i]);
            }

            Account account = cache.Get(_addresses[Capacity - 1]);
            Assert.AreEqual(_accounts[Capacity - 1], account);
        }
    }
}