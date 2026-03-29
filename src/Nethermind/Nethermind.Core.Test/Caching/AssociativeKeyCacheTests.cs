// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Caching;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test.Caching;

[TestFixture]
public class AssociativeKeyCacheTests
{
    private const int Capacity = 32;

    private readonly Address[] _addresses = new Address[Capacity * 2 + 1];
    private AddressAsKey[] _keys = [];

    [SetUp]
    public void Setup()
    {
        for (int i = 0; i < Capacity * 2 + 1; i++)
        {
            _addresses[i] = Build.An.Address.FromNumber(i).TestObject;
        }

        _keys = new AddressAsKey[Capacity * 2 + 1];
        for (int i = 0; i < _addresses.Length; i++)
        {
            _keys[i] = new AddressAsKey(_addresses[i]);
        }
    }

    [Test]
    public void At_capacity()
    {
        AssociativeKeyCache<AddressAsKey> cache = new(Capacity);
        for (int i = 0; i < Capacity; i++)
        {
            cache.Set(in _keys[i]);
        }

        cache.Get(in _keys[Capacity - 1]).Should().BeTrue();
    }

    [Test]
    public void Can_reset()
    {
        AssociativeKeyCache<AddressAsKey> cache = new(Capacity);
        cache.Set(in _keys[0]).Should().BeTrue();
        cache.Set(in _keys[0]).Should().BeFalse();
        cache.Get(in _keys[0]).Should().BeTrue();
    }

    [Test]
    public void Can_ask_before_first_set()
    {
        AssociativeKeyCache<AddressAsKey> cache = new(Capacity);
        cache.Get(in _keys[0]).Should().BeFalse();
    }

    [Test]
    public void Can_clear()
    {
        AssociativeKeyCache<AddressAsKey> cache = new(Capacity);
        cache.Set(in _keys[0]).Should().BeTrue();
        cache.Clear();
        cache.Get(in _keys[0]).Should().BeFalse();
        cache.Set(in _keys[0]).Should().BeTrue();
        cache.Get(in _keys[0]).Should().BeTrue();
    }

    [Test]
    public void Beyond_capacity()
    {
        AssociativeKeyCache<AddressAsKey> cache = new(Capacity);
        for (int i = 0; i < Capacity * 2; i++)
        {
            cache.Set(in _keys[i]);
        }

        // Count is bounded — cannot exceed physical capacity
        cache.Count.Should().BeLessOrEqualTo(Capacity);

        // At least some entries from the second half should survive
        int found = 0;
        for (int i = Capacity; i < Capacity * 2; i++)
        {
            if (cache.Get(in _keys[i])) found++;
        }
        found.Should().BeGreaterThan(0);
    }

    [Test]
    public void Beyond_capacity_stress()
    {
        AssociativeKeyCache<AddressAsKey> cache = new(Capacity);
        for (int round = 0; round < 4; round++)
        {
            for (int i = 0; i < Capacity * 2; i++)
            {
                cache.Set(in _keys[i]);
            }
        }
        // No exception — cache remains operational
        cache.Count.Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    public void Beyond_capacity_parallel()
    {
        AssociativeKeyCache<AddressAsKey> cache = new(Capacity);
        int processorCount = Math.Min(Environment.ProcessorCount, 32);
        Parallel.For(0, processorCount * 8, _ =>
        {
            for (int i = 0; i < Capacity; i++)
            {
                cache.Set(in _keys[i]);
            }
            for (int i = 0; i < Capacity; i++)
            {
                cache.Get(in _keys[i]);
            }
            for (int i = Capacity; i < Capacity * 2; i++)
            {
                cache.Set(in _keys[i]);
            }
            for (int i = 0; i < Capacity / 2; i++)
            {
                cache.Delete(in _keys[i]);
            }
        });
        // No crash — cache remains consistent
        cache.Count.Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    public void Can_delete()
    {
        AssociativeKeyCache<AddressAsKey> cache = new(Capacity);
        cache.Set(in _keys[0]).Should().BeTrue();
        cache.Delete(in _keys[0]).Should().BeTrue();
        cache.Get(in _keys[0]).Should().BeFalse();
        cache.Delete(in _keys[0]).Should().BeFalse();
    }

    [TestCase(-1)]
    [TestCase(134_217_729)]
    public void Capacity_out_of_range_throws(int capacity)
    {
        FluentActions.Invoking(() => new AssociativeKeyCache<AddressAsKey>(capacity))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void Capacity_zero()
    {
        AssociativeKeyCache<AddressAsKey> cache = new(0);
        for (int i = 0; i < Capacity * 2; i++)
        {
            // Null-object pattern: Set always returns true (as-if inserted) but nothing is stored
            cache.Set(in _keys[i]).Should().BeTrue();
        }

        for (int i = 0; i < Capacity * 2; i++)
        {
            cache.Get(in _keys[i]).Should().BeFalse();
        }

        cache.Count.Should().Be(0);
    }

    [Test]
    public void Clear_epoch_invalidates_entries()
    {
        AssociativeKeyCache<AddressAsKey> cache = new(Capacity);
        for (int i = 0; i < Capacity; i++)
        {
            cache.Set(in _keys[i]).Should().BeTrue();
        }

        cache.Clear();

        for (int i = 0; i < Capacity; i++)
        {
            cache.Get(in _keys[i]).Should().BeFalse();
        }

        cache.Count.Should().Be(0);
    }

    [Test]
    public void Contains_works()
    {
        AssociativeKeyCache<AddressAsKey> cache = new(Capacity);

        // Before insert: both miss
        cache.Contains(in _keys[0]).Should().BeFalse();
        cache.Get(in _keys[0]).Should().BeFalse();

        cache.Set(in _keys[0]);

        // After insert: both hit
        cache.Contains(in _keys[0]).Should().BeTrue();
        cache.Get(in _keys[0]).Should().BeTrue();

        cache.Delete(in _keys[0]);

        // After delete: both miss again
        cache.Contains(in _keys[0]).Should().BeFalse();
        cache.Get(in _keys[0]).Should().BeFalse();
    }
}
