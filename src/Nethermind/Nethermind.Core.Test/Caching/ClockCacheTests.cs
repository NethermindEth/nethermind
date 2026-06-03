// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core.Caching;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

using Cache = Nethermind.Core.Caching.ClockCache<Nethermind.Core.AddressAsKey, Nethermind.Core.Account>;

namespace Nethermind.Core.Test.Caching
{
    [TestFixture]
    public class ClockCacheTests
    {
        private static Cache Create() => new Cache(Capacity)!;

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
            Cache cache = Create();
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
            Cache cache = Create();
            Assert.That(cache.Set(_addresses[0], _accounts[0]), Is.True);
            Assert.That(cache.Set(_addresses[0], _accounts[1]), Is.False);
            Assert.That(cache.Get(_addresses[0]), Is.EqualTo(_accounts[1]));
        }

        [Test]
        public void Can_ask_before_first_set()
        {
            Cache cache = Create();
            Assert.That(cache.Get(_addresses[0]), Is.Null);
        }

        [Test]
        public void Can_clear()
        {
            Cache cache = Create();
            Assert.That(cache.Set(_addresses[0], _accounts[0]), Is.True);
            cache.Clear();
            Assert.That(cache.Get(_addresses[0]), Is.Null);
            Assert.That(cache.Set(_addresses[0], _accounts[1]), Is.True);
            Assert.That(cache.Get(_addresses[0]), Is.EqualTo(_accounts[1]));
        }

        [Test]
        public void Beyond_capacity()
        {
            Cache cache = Create();
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
        public void Beyond_capacity_lru()
        {
            Cache cache = Create();
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
            Cache cache = Create();
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
                        Assert.That(cache.Delete(_addresses[index]), Is.True);
                        Assert.That(cache.Set(_addresses[index], _accounts[index]), Is.True);
                    }
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
                        Assert.That(cache.Get(_addresses[ii]), Is.EqualTo(_accounts[ii]));
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
            Cache cache = new(Capacity);
            Parallel.For(0, Environment.ProcessorCount * 8, (iter) =>
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
        public void Can_set_and_then_set_null()
        {
            Cache cache = Create();
            Assert.That(cache.Set(_addresses[0], _accounts[0]), Is.True);
            Assert.That(cache.Set(_addresses[0], _accounts[0]), Is.False);
            Assert.That(cache.Set(_addresses[0], null!), Is.True);
            Assert.That(cache.Get(_addresses[0]), Is.EqualTo(null));
        }

        [Test]
        public void Can_delete()
        {
            Cache cache = Create();
            cache.Set(_addresses[0], _accounts[0]);
            Assert.That(cache.Delete(_addresses[0]), Is.True);
            Assert.That(cache.Get(_addresses[0]), Is.EqualTo(null));
            Assert.That(cache.Delete(_addresses[0]), Is.False);
        }

        [Test]
        public void Delete_returns_value()
        {
            Cache cache = Create();
            cache.Set(_addresses[0], _accounts[0]);

            Assert.That(cache.Delete(_addresses[0], out Account? value), Is.True);
            Assert.That(value, Is.EqualTo(_accounts[0]));

            Assert.That(cache.Delete(_addresses[0], out Account? noValue), Is.False);
            Assert.That(noValue, Is.Null);
        }

        [Test]
        public void Clear_should_free_all_capacity()
        {
            Cache cache = Create();
            for (int i = 0; i < Capacity; i++)
            {
                Assert.That(cache.Set(_addresses[i], _accounts[i]), Is.True);
            }

            cache.Clear();

            static int MapForRefill(int index) => (index + 1) % Capacity;

            // fill again
            for (int i = 0; i < Capacity; i++)
            {
                Assert.That(cache.Set(_addresses[i], _accounts[MapForRefill(i)]), Is.True);
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

            ClockCache<int, int> cache = new(maxCapacity);

            for (int i = 0; i < iterations; i++)
            {
                Assert.That(cache.Set(i, i), Is.True);
                int remove = i - itemsToKeep;
                if (remove >= 0)
                    Assert.That(cache.Delete(remove), Is.True);
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
        public void Capacity_zero()
        {
            ClockCache<AddressAsKey, int> cache = new(0);
            for (int i = 0; i < Capacity * 2; i++)
            {
                Assert.That(cache.Set(_addresses[i], 0), Is.True);
            }

            for (int i = 0; i < Capacity; i++)
            {
                Assert.That(cache.TryGet(_addresses[i], out _), Is.False);
            }
            // Check in reverse order
            for (int i = Capacity * 2 - 1; i >= Capacity; i--)
            {
                Assert.That(cache.TryGet(_addresses[i], out _), Is.False);
            }
        }
    }
}
