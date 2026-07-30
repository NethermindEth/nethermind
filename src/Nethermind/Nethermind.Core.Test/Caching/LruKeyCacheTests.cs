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
    public class LruKeyCacheTests
    {
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
            LruKeyCache<Address> cache = new(Capacity, "test");
            for (int i = 0; i < Capacity; i++)
            {
                Assert.That(cache.Set(_addresses[i]), Is.True);
            }

            Assert.That(cache.Get(_addresses[Capacity - 1]), Is.True);
        }

        [Test]
        public void Can_reset()
        {
            LruKeyCache<Address> cache = new(Capacity, "test");
            Assert.That(cache.Set(_addresses[0]), Is.True);
            Assert.That(cache.Set(_addresses[0]), Is.False);
            Assert.That(cache.Get(_addresses[0]), Is.True);
        }

        [Test]
        public void Can_ask_before_first_set()
        {
            LruKeyCache<Address> cache = new(Capacity, "test");
            Assert.That(cache.Get(_addresses[0]), Is.False);
        }

        [Test]
        public void Can_clear()
        {
            LruKeyCache<Address> cache = new(Capacity, "test");
            Assert.That(cache.Set(_addresses[0]), Is.True);
            cache.Clear();
            Assert.That(cache.Get(_addresses[0]), Is.False);
            Assert.That(cache.Set(_addresses[0]), Is.True);
            Assert.That(cache.Get(_addresses[0]), Is.True);
        }

        [Test]
        public void Beyond_capacity()
        {
            LruKeyCache<Address> cache = new(Capacity, "test");
            for (int i = 0; i < Capacity * 2; i++)
            {
                Assert.That(cache.Set(_addresses[i]), Is.True);
            }

            for (int i = 0; i < Capacity; i++)
            {
                Assert.That(cache.Get(_addresses[i]), Is.False);
            }
            // Check in reverse order
            for (int i = Capacity * 2 - 1; i >= Capacity; i--)
            {
                Assert.That(cache.Get(_addresses[i]), Is.True);
            }
        }

        [Test]
        public void Beyond_capacity_lru()
        {
            LruKeyCache<AddressAsKey> cache = new(Capacity, "test");
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
        public void Beyond_capacity_lru_check()
        {
            Random random = new();
            LruKeyCache<AddressAsKey> cache = new(Capacity, "test");
            for (int iter = 0; iter < Capacity; iter++)
            {
                for (int ii = 0; ii < Capacity; ii++)
                {
                    Assert.That(cache.Set(_addresses[ii]), Is.True);
                }

                for (int i = 1; i < Capacity; i++)
                {
                    for (int ii = i - 1; ii < i - 1 + Capacity; ii++)
                    {
                        // Fuzz the order of the addresses
                        int index = random.Next(i - 1, i - 1 + Capacity);
                        Assert.That(cache.Delete(_addresses[index]), Is.True);
                        Assert.That(cache.Set(_addresses[index]), Is.True);
                    }
                    for (int ii = i - 1; ii < i - 1 + Capacity; ii++)
                    {
                        // Fuzz the order of the addresses
                        int index = random.Next(i - 1, i - 1 + Capacity);
                        Assert.That(cache.Set(_addresses[index]), Is.False);
                    }
                    for (int ii = i - 1; ii < i - 1 + Capacity; ii++)
                    {
                        // Fuzz the order of the addresses
                        int index = random.Next(i - 1, i - 1 + Capacity);
                        Assert.That(cache.Get(_addresses[index]), Is.True);
                    }
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        if (ii < i + Capacity - 1)
                            Assert.That(cache.Set(_addresses[ii]), Is.False);
                        else
                            Assert.That(cache.Set(_addresses[ii]), Is.True);
                    }
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        Assert.That(cache.Get(_addresses[ii]), Is.True);
                    }
                    if (i > 0)
                    {
                        Assert.That(cache.Get(_addresses[i - 1]), Is.False);
                    }
                    Assert.That(cache.Get(_addresses[i + Capacity]), Is.False);
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
                        Assert.That(cache.Delete(_addresses[ii]), Is.True);
                    }
                }

                Assert.That(cache.Count, Is.EqualTo(0));
            }
        }

        [Test]
        public void Beyond_capacity_lru_parallel()
        {
            LruKeyCache<AddressAsKey> cache = new(Capacity, Capacity / 2, "test");
            int processorCount = Math.Min(Environment.ProcessorCount, 32);
            Parallel.For(0, processorCount * 8, (iter) =>
            {
                for (int ii = 0; ii < Capacity; ii++)
                {
                    cache.Set(_addresses[ii]);
                }

                for (int i = 1; i < Capacity; i++)
                {
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        cache.Set(_addresses[ii]);
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

                    if (iter % processorCount == 0)
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
        public void Can_delete()
        {
            LruKeyCache<Address> cache = new(Capacity, "test");
            Assert.That(cache.Set(_addresses[0]), Is.True);
            cache.Delete(_addresses[0]);
            Assert.That(cache.Get(_addresses[0]), Is.False);
        }
    }
}
