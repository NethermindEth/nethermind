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
using FluentAssertions;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.Caching
{
    [TestFixture(typeof(LruCache<Address, Account>))]
    public class LruCacheTests<TCache>
    {
        private static ICache<Address, Account> Create()
        {
            return (ICache<Address, Account>) Activator.CreateInstance(typeof(TCache), Capacity, "test");
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

        [Test]
        public void Clear_should_free_all_capacity()
        {
            ICache<Address, Account> cache = Create();
            for (int i = 0; i < Capacity; i++)
            {
                cache.Set(_addresses[i], _accounts[i]);
            }

            cache.Clear();

            static int MapForRefill (int index) => (index + 1) % Capacity;

            // fill again
            for (int i = 0; i < Capacity; i++)
            {
                cache.Set(_addresses[i], _accounts[MapForRefill(i)]);
            }

            // validate
            for (int i = 0; i < Capacity; i++)
            {
                cache.Get(_addresses[i]).Should().Be(_accounts[MapForRefill(i)]);
            }
        }
        
        [Test]
        public void Delete_keeps_internal_structure()
        {
            int maxCapacity = 32;
            int itemsToKeep = 10;
            int iterations = 40;
                
            LruCache<int, int> cache = new LruCache<int, int>(maxCapacity, "test");

            for (int i = 0; i < iterations; i++)
            {
                cache.Set(i, i);
                cache.Delete(i - itemsToKeep);
            }

            int count = 0;
            
            for (int i = 0; i < iterations; i++)
            {
                if (cache.TryGet(i, out int val))
                {
                    count++;
                    val.Should().Be(i);
                }
            }

            count.Should().Be(itemsToKeep);
        }

        [Test]
        public void Wrong_capacity_number_at_constructor()
        {
            int maxCapacity = 0;

            Assert.Throws<ArgumentOutOfRangeException>(() => 
                {
                    LruCache<int, int> cache = new LruCache<int, int>(maxCapacity, "test");
                });

        }
    }
}
