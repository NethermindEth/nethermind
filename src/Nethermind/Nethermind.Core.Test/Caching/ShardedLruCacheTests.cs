// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Caching;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.Caching
{
    [TestFixture(16, 64)]
    [TestFixture(1, 16)]
    public class ShardedLruCacheTests
    {
        public ShardedLruCacheTests(int shardCount, int capacity)
        {
            _shardCount = shardCount;
            _capacity = capacity;

            _accounts = new Account[_capacity * 2];
            _addresses = new Address[_capacity * 2];
        }

        private ICache<Address, Account> Create()
        {
            return new ShardedLruCache<Address, Account>(_capacity, _shardCount, "test");
        }

        private readonly Account[] _accounts;
        private readonly Address[] _addresses;
        private readonly int _shardCount;
        private readonly int _capacity;

        [SetUp]
        public void Setup()
        {
            for (int i = 0; i < _capacity * 2; i++)
            {
                _accounts[i] = Build.An.Account.WithBalance((UInt256)i).TestObject;
                _addresses[i] = Build.An.Address.FromNumber(i).TestObject;
            }
        }

        [Test]
        public void At_capacity()
        {
            ICache<Address, Account> cache = Create();
            for (int i = 0; i < _capacity; i++)
            {
                cache.Set(_addresses[i], _accounts[i]);
            }

            Account? account = cache.Get(_addresses[_capacity - 1]);
            Assert.That(account, Is.EqualTo(_accounts[_capacity - 1]));
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
            for (int i = 0; i < _capacity * 2; i++)
            {
                cache.Set(_addresses[i], _accounts[i]);
            }

            Account? account = cache.Get(_addresses[_capacity]);
            account.Should().Be(_accounts[_capacity]);
        }

        [Test]
        public void Can_set_and_then_set_null()
        {
            ICache<Address, Account> cache = Create();
            cache.Set(_addresses[0], _accounts[0]).Should().BeTrue();
            cache.Set(_addresses[0], _accounts[0]).Should().BeFalse();
            cache.Set(_addresses[0], null!).Should().BeTrue();
            cache.Get(_addresses[0]).Should().Be(null);
        }

        [Test]
        public void Can_delete()
        {
            ICache<Address, Account> cache = Create();
            cache.Set(_addresses[0], _accounts[0]);
            cache.Delete(_addresses[0]).Should().BeTrue();
            cache.Get(_addresses[0]).Should().Be(null);
            cache.Delete(_addresses[0]).Should().BeFalse();
        }

        [Test]
        public void Clear_should_free_all_capacity()
        {
            ICache<Address, Account> cache = Create();
            for (int i = 0; i < _capacity; i++)
            {
                cache.Set(_addresses[i], _accounts[i]);
            }

            cache.Clear();

            int MapForRefill(int index) => (index + 1) % _capacity;

            // fill again
            for (int i = 0; i < _capacity; i++)
            {
                cache.Set(_addresses[i], _accounts[MapForRefill(i)]);
            }

            // validate
            for (int i = 0; i < _capacity; i++)
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

            LruCache<int, int> cache = new(maxCapacity, "test");

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
                    LruCache<int, int> unused = new(maxCapacity, "test");
                });

        }
    }
}
