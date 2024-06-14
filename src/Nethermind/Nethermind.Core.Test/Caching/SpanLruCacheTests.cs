// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Caching;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.Caching
{
    public class SpanLruCacheTests<TCache>
    {
        private static ISpanCache<byte, Account> Create()
        {
            return new SpanLruCache<byte, Account>(Capacity, 0, "test", Bytes.SpanEqualityComparer)!;
        }

        private const int Capacity = 32;

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
                cache.Set(_addresses[i].Bytes, _accounts[i]).Should().BeTrue();
            }

            Account? account = cache.Get(_addresses[Capacity - 1].Bytes);
            Assert.That(account, Is.EqualTo(_accounts[Capacity - 1]));
        }

        [Test]
        public void Can_reset()
        {
            ISpanCache<byte, Account> cache = Create();
            cache.Set(_addresses[0].Bytes, _accounts[0]).Should().BeTrue();
            cache.Set(_addresses[0].Bytes, _accounts[1]).Should().BeFalse();
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
            cache.Set(_addresses[0].Bytes, _accounts[0]).Should().BeTrue();
            cache.Clear();
            cache.Get(_addresses[0].Bytes).Should().BeNull();
            cache.Set(_addresses[0].Bytes, _accounts[1]).Should().BeTrue();
            cache.Get(_addresses[0].Bytes).Should().Be(_accounts[1]);
        }

        [Test]
        public void Beyond_capacity()
        {
            ISpanCache<byte, Account> cache = Create();
            for (int i = 0; i < Capacity * 2; i++)
            {
                cache.Set(_addresses[i].Bytes, _accounts[i]).Should().BeTrue();
            }

            Account? account = cache.Get(_addresses[Capacity].Bytes);
            account.Should().Be(_accounts[Capacity]);
        }

        [Test]
        public void Beyond_capacity_lru()
        {
            ISpanCache<byte, Account> cache = Create();
            for (int i = 0; i < Capacity * 2; i++)
            {
                for (int ii = 0; ii < Capacity / 2; ii++)
                {
                    cache.Set(_addresses[i].Bytes, _accounts[i]);
                }
                cache.Set(_addresses[i].Bytes, _accounts[i]);
            }
        }

        [Test]
        public void Beyond_capacity_lru_check()
        {
            Random random = new();
            ISpanCache<byte, Account> cache = Create();
            for (var iter = 0; iter < Capacity; iter++)
            {
                for (int ii = 0; ii < Capacity; ii++)
                {
                    cache.Set(_addresses[ii].Bytes, _accounts[ii]).Should().BeTrue();
                }

                for (int i = 1; i < Capacity; i++)
                {
                    for (int ii = i - 1; ii < i - 1 + Capacity; ii++)
                    {
                        // Fuzz the order of the addresses
                        var index = random.Next(i - 1, i - 1 + Capacity);
                        cache.Delete(_addresses[index].Bytes).Should().BeTrue();
                        cache.Set(_addresses[index].Bytes, _accounts[index]).Should().BeTrue();
                    }
                    for (int ii = i - 1; ii < i - 1 + Capacity; ii++)
                    {
                        // Fuzz the order of the addresses
                        var index = random.Next(i - 1, i - 1 + Capacity);
                        cache.Set(_addresses[index].Bytes, _accounts[index]).Should().BeFalse();
                    }
                    for (int ii = i - 1; ii < i - 1 + Capacity; ii++)
                    {
                        // Fuzz the order of the addresses
                        var index = random.Next(i - 1, i - 1 + Capacity);
                        cache.Get(_addresses[index].Bytes).Should().BeEquivalentTo(_accounts[index]);
                    }
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        if (ii < i + Capacity - 1)
                            cache.Set(_addresses[ii].Bytes, _accounts[ii]).Should().BeFalse();
                        else
                            cache.Set(_addresses[ii].Bytes, _accounts[ii]).Should().BeTrue();
                    }
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        cache.Get(_addresses[ii].Bytes).Should().NotBeNull();
                        cache.Get(_addresses[ii].Bytes).Should().BeEquivalentTo(_accounts[ii]);
                    }
                    if (i > 0)
                    {
                        cache.Get(_addresses[i - 1].Bytes).Should().BeNull();
                    }
                    cache.Get(_addresses[i + Capacity].Bytes).Should().BeNull();
                }

                cache.Count.Should().Be(Capacity);
                if (iter % 2 == 0)
                {
                    cache.Clear();
                }
                else
                {
                    for (int ii = Capacity - 1; ii < Capacity * 2 - 1; ii++)
                    {
                        cache.Get(_addresses[ii].Bytes).Should().BeEquivalentTo(_accounts[ii]);
                        cache.Delete(_addresses[ii].Bytes).Should().BeTrue();
                    }
                }

                cache.Count.Should().Be(0);
            }
        }

        [Test]
        public void Beyond_capacity_lru_parallel()
        {
            ISpanCache<byte, Account> cache = Create();
            Parallel.For(0, Environment.ProcessorCount * 8, (iter) =>
            {
                for (int ii = 0; ii < Capacity; ii++)
                {
                    cache.Set(_addresses[ii].Bytes, _accounts[ii]);
                }

                for (int i = 1; i < Capacity; i++)
                {
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        cache.Set(_addresses[ii].Bytes, _accounts[ii]);
                    }
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        cache.Get(_addresses[ii].Bytes);
                    }
                    if (i > 0)
                    {
                        cache.Get(_addresses[i - 1].Bytes);
                    }
                    cache.Get(_addresses[i + Capacity].Bytes);

                    if (iter % Environment.ProcessorCount == 0)
                    {
                        cache.Clear();
                    }
                    else
                    {
                        for (int ii = i; ii < i + Capacity / 2; ii++)
                        {
                            cache.Delete(_addresses[ii].Bytes);
                        }
                    }
                }
            });
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
            cache.Set(_addresses[0].Bytes, _accounts[0]).Should().BeTrue();
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
