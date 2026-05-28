// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core.Caching;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.Caching
{
    [TestFixture]
    public class LruCacheTests
    {
        private static ICache<Address, Account> Create() => new LruCache<Address, Account>(Capacity, Capacity / 2, "test");

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
                Assert.That(cache.Set(_addresses[i], _accounts[i]), Is.True);
            }

            Account? account = cache.Get(_addresses[Capacity - 1]);
            Assert.That(account, Is.EqualTo(_accounts[Capacity - 1]));
        }

        [Test]
        public void Can_reset()
        {
            ICache<Address, Account> cache = Create();
            Assert.That(cache.Set(_addresses[0], _accounts[0]), Is.True);
            Assert.That(cache.Set(_addresses[0], _accounts[1]), Is.False);
            Assert.That(cache.Get(_addresses[0]), Is.EqualTo(_accounts[1]));
        }

        [Test]
        public void Can_ask_before_first_set()
        {
            ICache<Address, Account> cache = Create();
            Assert.That(cache.Get(_addresses[0]), Is.Null);
        }

        [Test]
        public void Can_clear()
        {
            ICache<Address, Account> cache = Create();
            Assert.That(cache.Set(_addresses[0], _accounts[0]), Is.True);
            cache.Clear();
            Assert.That(cache.Get(_addresses[0]), Is.Null);
            Assert.That(cache.Set(_addresses[0], _accounts[1]), Is.True);
            Assert.That(cache.Get(_addresses[0]), Is.EqualTo(_accounts[1]));
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
            for (int iter = 0; iter < Capacity; iter++)
            {
                for (int ii = 0; ii < Capacity; ii++)
                {
                    Assert.That(cache.Set(_addresses[ii], _accounts[ii]), Is.True);
                }

                for (int i = 1; i < Capacity; i++)
                {
                    for (int ii = i - 1; ii < i - 1 + Capacity; ii++)
                    {
                        // Fuzz the order of the addresses
                        int index = random.Next(i - 1, i - 1 + Capacity);
                        Assert.That(cache.Set(_addresses[index], _accounts[index]), Is.False);
                    }
                    for (int ii = i - 1; ii < i - 1 + Capacity; ii++)
                    {
                        // Fuzz the order of the addresses
                        int index = random.Next(i - 1, i - 1 + Capacity);
                        Assert.That(cache.Delete(_addresses[index]), Is.True);
                        Assert.That(cache.Set(_addresses[index], _accounts[index]), Is.True);
                    }
                    for (int ii = i - 1; ii < i - 1 + Capacity; ii++)
                    {
                        // Fuzz the order of the addresses
                        int index = random.Next(i - 1, i - 1 + Capacity);
                        Assert.That(cache.Get(_addresses[index]), Is.EqualTo(_accounts[index]));
                    }
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        if (ii < i + Capacity - 1)
                            Assert.That(cache.Set(_addresses[ii], _accounts[ii]), Is.False);
                        else
                            Assert.That(cache.Set(_addresses[ii], _accounts[ii]), Is.True);
                    }
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        Assert.That(cache.Get(_addresses[ii]), Is.Not.Null);
                    }
                    if (i > 0)
                    {
                        Assert.That(cache.Get(_addresses[i - 1]), Is.Null);
                    }
                    Assert.That(cache.Get(_addresses[i + Capacity]), Is.Null);
                }

                Assert.That(cache.Count, Is.EqualTo(Capacity));
                if (iter % 2 == 0)
                {
                    cache.Clear();
                }
                else
                {
                    for (int ii = Capacity - 1; ii < Capacity * 2 - 1; ii++)
                    {
                        Assert.That(cache.Get(_addresses[ii]), Is.EqualTo(_accounts[ii]));
                        Assert.That(cache.Delete(_addresses[ii]), Is.True);
                    }
                }

                Assert.That(cache.Count, Is.EqualTo(0));
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
                Assert.That(cache.Set(_addresses[i], _accounts[i]), Is.True);
            }

            for (int i = 0; i < Capacity; i++)
            {
                Assert.That(cache.Get(_addresses[i]), Is.Null);
            }
            // Check in reverse order
            for (int i = Capacity * 2 - 1; i >= Capacity; i--)
            {
                Assert.That(cache.Get(_addresses[i]), Is.EqualTo(_accounts[i]));
            }
        }

        [Test]
        public void Can_set_and_then_set_null()
        {
            ICache<Address, Account> cache = Create();
            Assert.That(cache.Set(_addresses[0], _accounts[0]), Is.True);
            Assert.That(cache.Set(_addresses[0], _accounts[0]), Is.False);
            Assert.That(cache.Set(_addresses[0], null!), Is.True);
            Assert.That(cache.Get(_addresses[0]), Is.EqualTo(null));
        }

        [Test]
        public void Can_delete()
        {
            ICache<Address, Account> cache = Create();
            cache.Set(_addresses[0], _accounts[0]);
            Assert.That(cache.Delete(_addresses[0]), Is.True);
            Assert.That(cache.Get(_addresses[0]), Is.EqualTo(null));
            Assert.That(cache.Delete(_addresses[0]), Is.False);
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
                Assert.That(cache.Get(_addresses[i]), Is.EqualTo(_accounts[MapForRefill(i)]));
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
                    Assert.That(val, Is.EqualTo(i));
                }
            }

            Assert.That(count, Is.EqualTo(itemsToKeep));
        }

        [Test]
        public void Wrong_capacity_number_at_constructor()
        {
            int maxCapacity = 0;

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                {
                    _ = new LruCache<int, int>(maxCapacity, "test");
                });

        }
    }
}
