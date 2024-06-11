// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Caching;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.Caching
{
    [TestFixture]
    public class LruKeyCacheNonConcurrentTests
    {
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
            LruKeyCacheNonConcurrent<Address> cache = new(Capacity, "test");
            for (int i = 0; i < Capacity; i++)
            {
                cache.Set(_addresses[i]).Should().BeTrue();
            }

            cache.Get(_addresses[Capacity - 1]).Should().BeTrue();
        }

        [Test]
        public void Can_reset()
        {
            LruKeyCacheNonConcurrent<Address> cache = new(Capacity, "test");
            cache.Set(_addresses[0]).Should().BeTrue();
            cache.Set(_addresses[0]).Should().BeFalse();
            cache.Get(_addresses[0]).Should().BeTrue();
        }

        [Test]
        public void Can_ask_before_first_set()
        {
            LruKeyCacheNonConcurrent<Address> cache = new(Capacity, "test");
            cache.Get(_addresses[0]).Should().BeFalse();
        }

        [Test]
        public void Can_clear()
        {
            LruKeyCacheNonConcurrent<Address> cache = new(Capacity, "test");
            cache.Set(_addresses[0]).Should().BeTrue();
            cache.Clear();
            cache.Get(_addresses[0]).Should().BeFalse();
            cache.Set(_addresses[0]).Should().BeTrue();
            cache.Get(_addresses[0]).Should().BeTrue();
        }

        [Test]
        public void Beyond_capacity()
        {
            LruKeyCacheNonConcurrent<Address> cache = new(Capacity, "test");
            for (int i = 0; i < Capacity * 2; i++)
            {
                cache.Set(_addresses[i]);
            }

            cache.Get(_addresses[Capacity]).Should().BeTrue();
        }

        [Test]
        public void Beyond_capacity_lru()
        {
            LruKeyCacheNonConcurrent<AddressAsKey> cache = new(Capacity, "test");
            for (int i = 0; i < Capacity * 2; i++)
            {
                for (int ii = 0; ii < Capacity / 2; ii++)
                {
                    cache.Set(_addresses[i]);
                }
                cache.Set(_addresses[i]);
            }
        }

        [Test]
        public void Can_delete()
        {
            LruKeyCacheNonConcurrent<Address> cache = new(Capacity, "test");
            cache.Set(_addresses[0]).Should().BeTrue();
            cache.Delete(_addresses[0]);
            cache.Get(_addresses[0]).Should().BeFalse();
        }
    }
}
