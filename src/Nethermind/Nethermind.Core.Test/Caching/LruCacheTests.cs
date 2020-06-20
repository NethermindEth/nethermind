//  Copyright (c) 2018 Demerzel Solutions Limited
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

using FluentAssertions;
using Nethermind.Core.Caching;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;

namespace Nethermind.Core.Test.Caching
{
    [TestFixture(true)]
    [TestFixture(false)]
    public class LruCacheTests
    {
        private readonly bool _allowRecycling;

        public LruCacheTests(bool allowRecycling)
        {
            _allowRecycling = allowRecycling;
        }
        
        private ICache<Address, Account> Create()
        {
            return _allowRecycling ? (ICache<Address, Account>)new LruCacheWithRecycling<Address, Account>(Capacity, "test") : new LruCache<Address, Account>(Capacity, "test");
        }
        
        private const int Capacity = 16;

        private readonly Account[] _accounts = new Account[Capacity * 2];
        private readonly Address[] _addresses = new Address[Capacity * 2];

        [SetUp]
        public void Setup()
        {
            for (int i = 0; i < Capacity * 2; i++)
            {
                _accounts[i] = Build.An.Account.WithBalance((UInt256)i).TestObject;
                _addresses[i] = Build.An.Address.FromNumber(i).TestObject;
            }
        }

        [Test]
        public void At_capacity()
        {
            ICache<Address, Account> cache = Create();
            for (int i = 0; i < Capacity; i++)
            {
                cache.Set(_addresses[i], _accounts[i]);
            }

            Account account = cache.Get(_addresses[Capacity - 1]);
            Assert.AreEqual(_accounts[Capacity - 1], account);
        }
        
        [Test]
        public void Can_reset()
        {
            ICache<Address, Account> cache = Create();
            cache.Set(_addresses[0], _accounts[0]);
            cache.Set(_addresses[0], _accounts[1]);
            cache.Get(_addresses[0]).Should().Be(_accounts[1]);
        }
        
        [Test]
        public void Can_ask_before_first_set()
        {
            ICache<Address, Account> cache = Create();
            cache.Get(_addresses[0]).Should().BeNull();
        }
        
        [Test]
        public void Can_clear()
        {
            ICache<Address, Account> cache = Create();
            cache.Set(_addresses[0], _accounts[0]);
            cache.Clear();
            cache.Get(_addresses[0]).Should().BeNull();
            cache.Set(_addresses[0], _accounts[1]);
            cache.Get(_addresses[0]).Should().Be(_accounts[1]);
        }
        
        [Test]
        public void Beyond_capacity()
        {
            ICache<Address, Account> cache = Create();
            for (int i = 0; i < Capacity * 2; i++)
            {
                cache.Set(_addresses[i], _accounts[i]);
            }

            Account account = cache.Get(_addresses[Capacity]);
            account.Should().Be(_accounts[Capacity]);
        }
        
        [Test]
        public void Can_set_and_then_set_null()
        {
            ICache<Address, Account> cache = Create();
            cache.Set(_addresses[0], _accounts[0]);
            cache.Set(_addresses[0], null);
            cache.Get(_addresses[0]).Should().Be(null);
        }
        
        [Test]
        public void Can_delete()
        {
            ICache<Address, Account> cache = Create();
            cache.Set(_addresses[0], _accounts[0]);
            cache.Delete(_addresses[0]);
            cache.Get(_addresses[0]).Should().Be(null);
        }
    }
}