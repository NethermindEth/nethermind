// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Core.Caching;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.Caching
{
    [TestFixture(typeof(SpanLruCache<byte, Account>))]
    public class SpanLruCacheTests<TCache>
    {
        private static ISpanCache<byte, Account> Create()
        {
            return (ISpanCache<byte, Account>)Activator.CreateInstance(typeof(TCache), Capacity, 0, "test", Bytes.SpanEqualityComparer)!;
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
            ISpanCache<byte, Account> cache = Create();
            for (int i = 0; i < Capacity; i++)
            {
                cache.Set(_addresses[i].Bytes, _accounts[i]);
            }

            Account? account = cache.Get(_addresses[Capacity - 1].Bytes);
            Assert.AreEqual(_accounts[Capacity - 1], account);
        }

        [Test]
        public void Can_reset()
        {
            ISpanCache<byte, Account> cache = Create();
            cache.Set(_addresses[0].Bytes, _accounts[0]);
            cache.Set(_addresses[0].Bytes, _accounts[1]);
            cache.Get(_addresses[0].Bytes).Should().Be(_accounts[1]);
        }

        [Test]
        public void Can_ask_before_first_set()
        {
            ISpanCache<byte, Account> cache = Create();
            cache.Get(_addresses[0].Bytes).Should().BeNull();
        }

        [Test]
        public void Can_clear()
        {
            ISpanCache<byte, Account> cache = Create();
            cache.Set(_addresses[0].Bytes, _accounts[0]);
            cache.Clear();
            cache.Get(_addresses[0].Bytes).Should().BeNull();
            cache.Set(_addresses[0].Bytes, _accounts[1]);
            cache.Get(_addresses[0].Bytes).Should().Be(_accounts[1]);
        }

        [Test]
        public void Beyond_capacity()
        {
            ISpanCache<byte, Account> cache = Create();
            for (int i = 0; i < Capacity * 2; i++)
            {
                cache.Set(_addresses[i].Bytes, _accounts[i]);
            }

            Account? account = cache.Get(_addresses[Capacity].Bytes);
            account.Should().Be(_accounts[Capacity]);
        }

        [Test]
        public void Can_set_and_then_set_null()
        {
            ISpanCache<byte, Account> cache = Create();
            cache.Set(_addresses[0].Bytes, _accounts[0]).Should().BeTrue();
            cache.Set(_addresses[0].Bytes, _accounts[0]).Should().BeFalse();
            cache.Set(_addresses[0].Bytes, null!).Should().BeTrue();
            cache.Get(_addresses[0].Bytes).Should().Be(null);
        }

        [Test]
        public void Can_delete()
        {
            ISpanCache<byte, Account> cache = Create();
            cache.Set(_addresses[0].Bytes, _accounts[0]);
            cache.Delete(_addresses[0].Bytes).Should().BeTrue();
            cache.Get(_addresses[0].Bytes).Should().Be(null);
            cache.Delete(_addresses[0].Bytes).Should().BeFalse();
        }

        [Test]
        public void Clear_should_free_all_capacity()
        {
            ISpanCache<byte, Account> cache = Create();
            for (int i = 0; i < Capacity; i++)
            {
                cache.Set(_addresses[i].Bytes, _accounts[i]);
            }

            cache.Clear();

            static int MapForRefill(int index) => (index + 1) % Capacity;

            // fill again
            for (int i = 0; i < Capacity; i++)
            {
                cache.Set(_addresses[i].Bytes, _accounts[MapForRefill(i)]);
            }

            // validate
            for (int i = 0; i < Capacity; i++)
            {
                cache.Get(_addresses[i].Bytes).Should().Be(_accounts[MapForRefill(i)]);
            }
        }

        [Test]
        public void Delete_keeps_internal_structure()
        {
            int maxCapacity = 32;
            int itemsToKeep = 10;
            int iterations = 40;

            SpanLruCache<byte, int> cache = new(maxCapacity, 0, "test", Bytes.SpanEqualityComparer);

            for (int i = 0; i < iterations; i++)
            {
                cache.Set(i.ToBigEndianByteArray(), i);
                cache.Delete((i - itemsToKeep).ToBigEndianByteArray());
            }

            int count = 0;

            for (int i = 0; i < iterations; i++)
            {
                if (cache.TryGet(i.ToBigEndianByteArray(), out int val))
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
                    SpanLruCache<byte, int> unused = new(maxCapacity, 0, "test", Bytes.SpanEqualityComparer);
                });

        }
    }
}
