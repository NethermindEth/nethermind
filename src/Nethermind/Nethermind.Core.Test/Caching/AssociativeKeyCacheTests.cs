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
    public void Clear_invalidates_and_frees_capacity()
    {
        AssociativeKeyCache<AddressAsKey> cache = new(Capacity);

        for (int i = 0; i < Capacity; i++)
        {
            cache.Set(in _keys[i]).Should().BeTrue();
        }

        cache.Clear();

        // All entries invisible, count reset
        for (int i = 0; i < Capacity; i++)
        {
            cache.Get(in _keys[i]).Should().BeFalse();
        }
        cache.Count.Should().Be(0);

        // Capacity is free — re-insert succeeds
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

    [TestCase(8)]
    [TestCase(32)]
    [TestCase(256)]
    [TestCase(1024)]
    public void All_inserted_keys_retrievable_at_various_capacities(int capacity)
    {
        // Insert only 50% of capacity to avoid set-conflict eviction.
        AssociativeKeyCache<AddressAsKey> cache = new(capacity);
        int insertCount = Math.Min(capacity / 2, _keys.Length - 1);

        for (int i = 0; i < insertCount; i++)
        {
            cache.Set(in _keys[i]).Should().BeTrue();
        }

        for (int i = 0; i < insertCount; i++)
        {
            cache.Get(in _keys[i]).Should().BeTrue($"key {i} should be present at capacity {capacity}");
        }

        cache.Count.Should().Be(insertCount);
    }

    [Test]
    public void Concurrent_clear_does_not_corrupt_count()
    {
        AssociativeKeyCache<AddressAsKey> cache = new(Capacity);

        Parallel.For(0, Environment.ProcessorCount * 4, iter =>
        {
            for (int i = 0; i < Capacity; i++)
            {
                cache.Set(in _keys[i]);
            }

            if (iter % 3 == 0)
            {
                cache.Clear();
            }
        });

        int count = cache.Count;
        count.Should().BeGreaterThanOrEqualTo(0);
        count.Should().BeLessOrEqualTo(Capacity);
    }

    [TestCase(-1)]
    [TestCase(134_217_729)]
    public void Capacity_out_of_range_throws(int capacity)
    {
        FluentActions.Invoking(() => new AssociativeKeyCache<AddressAsKey>(capacity))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestCase(0)]
    [TestCase(4096)]
    public void Capacity_valid_boundary(int capacity)
    {
        AssociativeKeyCache<AddressAsKey> cache = new(capacity);

        cache.Set(in _keys[0]);

        if (capacity == 0)
        {
            cache.Get(in _keys[0]).Should().BeFalse();
            cache.Count.Should().Be(0);
        }
        else
        {
            cache.Get(in _keys[0]).Should().BeTrue();
            cache.Count.Should().Be(1);
        }
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
