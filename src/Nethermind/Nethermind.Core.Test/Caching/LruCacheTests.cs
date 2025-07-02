// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Caching;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.Caching
{
    [TestFixture]
    public class LruCacheTests
    {
        private static ICache<Address, Account> Create()
        {
            return new LruCache<Address, Account>(Capacity, Capacity / 2, "test");
        }

        private const int Capacity = 32;

        private readonly Account[] _accounts = new Account[Capacity * 2 + 1];
        private readonly Address[] _addresses = new Address[Capacity * 2 + 1];

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
                cache.Set(_addresses[i], _accounts[i]).Should().BeTrue();
            }

            Account? account = cache.Get(_addresses[Capacity - 1]);
            Assert.That(account, Is.EqualTo(_accounts[Capacity - 1]));
        }

        [Test]
        public void Can_reset()
        {
            ICache<Address, Account> cache = Create();
            cache.Set(_addresses[0], _accounts[0]).Should().BeTrue();
            cache.Set(_addresses[0], _accounts[1]).Should().BeFalse();
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
            cache.Set(_addresses[0], _accounts[0]).Should().BeTrue();
            cache.Clear();
            cache.Get(_addresses[0]).Should().BeNull();
            cache.Set(_addresses[0], _accounts[1]).Should().BeTrue();
            cache.Get(_addresses[0]).Should().Be(_accounts[1]);
        }

        [Test]
        public void Beyond_capacity_lru()
        {
            ICache<Address, Account> cache = Create();
            for (int i = 0; i < Capacity * 2; i++)
            {
                for (int ii = 0; ii < Capacity / 2; ii++)
                {
                    cache.Set(_addresses[i], _accounts[i]);
                }
                cache.Set(_addresses[i], _accounts[i]);
            }
        }

        [Test]
        public void Beyond_capacity_lru_check()
        {
            Random random = new();
            ICache<Address, Account> cache = Create();
            for (var iter = 0; iter < Capacity; iter++)
            {
                for (int ii = 0; ii < Capacity; ii++)
                {
                    cache.Set(_addresses[ii], _accounts[ii]).Should().BeTrue();
                }

                for (int i = 1; i < Capacity; i++)
                {
                    for (int ii = i - 1; ii < i - 1 + Capacity; ii++)
                    {
                        // Fuzz the order of the addresses
                        var index = random.Next(i - 1, i - 1 + Capacity);
                        cache.Set(_addresses[index], _accounts[index]).Should().BeFalse();
                    }
                    for (int ii = i - 1; ii < i - 1 + Capacity; ii++)
                    {
                        // Fuzz the order of the addresses
                        var index = random.Next(i - 1, i - 1 + Capacity);
                        cache.Delete(_addresses[index]).Should().BeTrue();
                        cache.Set(_addresses[index], _accounts[index]).Should().BeTrue();
                    }
                    for (int ii = i - 1; ii < i - 1 + Capacity; ii++)
                    {
                        // Fuzz the order of the addresses
                        var index = random.Next(i - 1, i - 1 + Capacity);
                        cache.Get(_addresses[index]).Should().BeEquivalentTo(_accounts[index]);
                    }
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        if (ii < i + Capacity - 1)
                            cache.Set(_addresses[ii], _accounts[ii]).Should().BeFalse();
                        else
                            cache.Set(_addresses[ii], _accounts[ii]).Should().BeTrue();
                    }
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        cache.Get(_addresses[ii]).Should().NotBeNull();
                    }
                    if (i > 0)
                    {
                        cache.Get(_addresses[i - 1]).Should().BeNull();
                    }
                    cache.Get(_addresses[i + Capacity]).Should().BeNull();
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
                        cache.Get(_addresses[ii]).Should().BeEquivalentTo(_accounts[ii]);
                        cache.Delete(_addresses[ii]).Should().BeTrue();
                    }
                }

                cache.Count.Should().Be(0);
            }
        }

        [Test]
        public void Beyond_capacity_lru_parallel()
        {
            ICache<Address, Account> cache = Create();
            Parallel.For(0, Math.Min(Environment.ProcessorCount * 8, 64), (iter) =>
            {
                for (int ii = 0; ii < Capacity; ii++)
                {
                    cache.Set(_addresses[ii], _accounts[ii]);
                }

                for (int i = 1; i < Capacity; i++)
                {
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        cache.Set(_addresses[ii], _accounts[ii]);
                    }
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        cache.Get(_addresses[ii]);
                    }
                    if (i > 0)
                    {
                        cache.Get(_addresses[i - 1]);
                    }
                    cache.Get(_addresses[i + Capacity]);

                    if (iter % Environment.ProcessorCount == 0)
                    {
                        cache.Clear();
                    }
                    else
                    {
                        for (int ii = i; ii < i + Capacity / 2; ii++)
                        {
                            cache.Delete(_addresses[ii]);
                        }
                    }
                }
            });
        }

        [Test]
        public void Beyond_capacity()
        {
            ICache<Address, Account> cache = Create();
            for (int i = 0; i < Capacity * 2; i++)
            {
                cache.Set(_addresses[i], _accounts[i]).Should().BeTrue();
            }

            for (int i = 0; i < Capacity; i++)
            {
                cache.Get(_addresses[i]).Should().BeNull();
            }
            // Check in reverse order
            for (int i = Capacity * 2 - 1; i >= Capacity; i--)
            {
                cache.Get(_addresses[i]).Should().Be(_accounts[i]);
            }
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
            for (int i = 0; i < Capacity; i++)
            {
                cache.Set(_addresses[i], _accounts[i]);
            }

            cache.Clear();

            static int MapForRefill(int index) => (index + 1) % Capacity;

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
