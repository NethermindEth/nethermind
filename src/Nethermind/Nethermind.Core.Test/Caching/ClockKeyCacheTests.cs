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
    public class ClockKeyCacheTests
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
            ClockKeyCache<AddressAsKey> cache = new(Capacity);
            for (int i = 0; i < Capacity; i++)
            {
                cache.Set(_addresses[i]);
            }

            cache.Get(_addresses[Capacity - 1]).Should().BeTrue();
        }

        [Test]
        public void Can_reset()
        {
            ClockKeyCache<AddressAsKey> cache = new(Capacity);
            cache.Set(_addresses[0]).Should().BeTrue();
            cache.Set(_addresses[0]).Should().BeFalse();
            cache.Get(_addresses[0]).Should().BeTrue();
        }

        [Test]
        public void Can_ask_before_first_set()
        {
            ClockKeyCache<AddressAsKey> cache = new(Capacity);
            cache.Get(_addresses[0]).Should().BeFalse();
        }

        [Test]
        public void Can_clear()
        {
            ClockKeyCache<AddressAsKey> cache = new(Capacity);
            cache.Set(_addresses[0]).Should().BeTrue();
            cache.Clear();
            cache.Get(_addresses[0]).Should().BeFalse();
            cache.Set(_addresses[0]).Should().BeTrue();
            cache.Get(_addresses[0]).Should().BeTrue();
        }

        [Test]
        public void Beyond_capacity()
        {
            ClockKeyCache<AddressAsKey> cache = new(Capacity);
            for (int i = 0; i < Capacity * 2; i++)
            {
                cache.Set(_addresses[i]);
            }

            for (int i = 0; i < Capacity; i++)
            {
                cache.Get(_addresses[i]).Should().BeFalse();
            }
            // Check in reverse order
            for (int i = Capacity * 2 - 1; i >= Capacity; i--)
            {
                cache.Get(_addresses[i]).Should().BeTrue();
            }
        }

        [Test]
        public void Beyond_capacity_lru()
        {
            ClockKeyCache<AddressAsKey> cache = new(Capacity);
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
            _ = new Random();
            ClockKeyCache<AddressAsKey> cache = new(Capacity);
            for (var iter = 0; iter < Capacity; iter++)
            {
                for (int ii = 0; ii < Capacity; ii++)
                {
                    cache.Set(_addresses[ii]).Should().BeTrue();
                }

                for (int i = 1; i < Capacity; i++)
                {
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        if (ii < i + Capacity - 1)
                            cache.Set(_addresses[ii]).Should().BeFalse();
                        else
                            cache.Set(_addresses[ii]).Should().BeTrue();
                    }
                    for (int ii = i; ii < i + Capacity; ii++)
                    {
                        cache.Get(_addresses[ii]).Should().BeTrue();
                    }
                    if (i > 0)
                    {
                        cache.Get(_addresses[i - 1]).Should().BeFalse();
                    }
                    cache.Get(_addresses[i + Capacity]).Should().BeFalse();
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
                        cache.Delete(_addresses[ii]).Should().BeTrue();
                    }
                }

                cache.Count.Should().Be(0);
            }
        }

        [Test]
        public void Beyond_capacity_lru_parallel()
        {
            ClockKeyCache<AddressAsKey> cache = new(Capacity);
            int processorCount = Math.Min(Environment.ProcessorCount, 32);
            Parallel.For(0, processorCount * 8, (s) =>
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

                    for (int ii = i; ii < i + Capacity / 2; ii++)
                    {
                        cache.Delete(_addresses[ii]);
                    }
                }
            });
        }

        [Test]
        public void Can_delete()
        {
            ClockKeyCache<AddressAsKey> cache = new(Capacity);
            cache.Set(_addresses[0]);
            cache.Delete(_addresses[0]);
            cache.Get(_addresses[0]).Should().BeFalse();
        }

        [Test]
        public void Capacity_zero()
        {
            ClockKeyCache<AddressAsKey> cache = new(0);
            for (int i = 0; i < Capacity * 2; i++)
            {
                cache.Set(_addresses[i]).Should().BeTrue();
            }

            for (int i = 0; i < Capacity; i++)
            {
                cache.Get(_addresses[i]).Should().BeFalse();
            }
            // Check in reverse order
            for (int i = Capacity * 2 - 1; i >= Capacity; i--)
            {
                cache.Get(_addresses[i]).Should().BeFalse();
            }
        }
    }
}
