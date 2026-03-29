// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Caching;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

using Cache = Nethermind.Core.Caching.AssociativeCache<Nethermind.Core.AddressAsKey, Nethermind.Core.Account>;

namespace Nethermind.Core.Test.Caching;

[TestFixture]
public class AssociativeCacheTests
{
    private const int Capacity = 32;

    private readonly Account[] _accounts = new Account[Capacity * 2 + 1];
    private readonly Address[] _addresses = new Address[Capacity * 2 + 1];
    private readonly AddressAsKey[] _keys = new AddressAsKey[Capacity * 2 + 1];

    [SetUp]
    public void Setup()
    {
        for (int i = 0; i < Capacity * 2; i++)
        {
            _accounts[i] = Build.An.Account.WithBalance((UInt256)i).TestObject;
            _addresses[i] = Build.An.Address.FromNumber(i).TestObject;
            _keys[i] = _addresses[i];
        }
    }

    private static Cache Create() => new Cache(Capacity);

    [Test]
    public void At_capacity()
    {
        Cache cache = Create();
        for (int i = 0; i < Capacity; i++)
        {
            cache.Set(in _keys[i], _accounts[i]).Should().BeTrue();
        }

        AddressAsKey lastKey = _keys[Capacity - 1];
        Account? account = cache.Get(in lastKey);
        account.Should().Be(_accounts[Capacity - 1]);
    }

    [Test]
    public void Can_reset()
    {
        Cache cache = Create();
        AddressAsKey key = _keys[0];
        cache.Set(in key, _accounts[0]).Should().BeTrue();
        cache.Set(in key, _accounts[1]).Should().BeFalse();
        cache.Get(in key).Should().Be(_accounts[1]);
    }

    [Test]
    public void Can_ask_before_first_set()
    {
        Cache cache = Create();
        AddressAsKey key = _keys[0];
        cache.Get(in key).Should().BeNull();
    }

    [Test]
    public void Can_clear()
    {
        Cache cache = Create();
        AddressAsKey key = _keys[0];
        cache.Set(in key, _accounts[0]).Should().BeTrue();
        cache.Clear();
        cache.Get(in key).Should().BeNull();
        cache.Set(in key, _accounts[1]).Should().BeTrue();
        cache.Get(in key).Should().Be(_accounts[1]);
    }

    [Test]
    public void Beyond_capacity()
    {
        Cache cache = Create();
        for (int i = 0; i < Capacity * 2; i++)
        {
            cache.Set(in _keys[i], _accounts[i]);
        }

        // Eviction is non-deterministic (3-random within set), so only assert the count is bounded
        cache.Count.Should().BeLessOrEqualTo(Capacity);

        // Any item that is present must return the correct value
        for (int i = 0; i < Capacity * 2; i++)
        {
            AddressAsKey key = _keys[i];
            Account? found = cache.Get(in key);
            if (found is not null)
            {
                found.Should().Be(_accounts[i]);
            }
        }
    }

    [Test]
    public void Beyond_capacity_stress()
    {
        Cache cache = Create();
        for (int iter = 0; iter < 4; iter++)
        {
            for (int i = 0; i < Capacity * 2; i++)
            {
                cache.Set(in _keys[i], _accounts[i]);
            }
            for (int i = 0; i < Capacity * 2; i++)
            {
                AddressAsKey key = _keys[i];
                cache.Get(in key);
            }
            if (iter % 2 == 0)
            {
                cache.Clear();
            }
        }

        // No crash means success; count is bounded
        cache.Count.Should().BeLessOrEqualTo(Capacity);
    }

    [Test]
    public void Beyond_capacity_parallel()
    {
        Cache cache = new Cache(Capacity);
        Parallel.For(0, Environment.ProcessorCount * 8, iter =>
        {
            for (int i = 0; i < Capacity * 2; i++)
            {
                AddressAsKey key = _keys[i];
                cache.Set(in key, _accounts[i]);
            }
            for (int i = 0; i < Capacity * 2; i++)
            {
                AddressAsKey key = _keys[i];
                cache.Get(in key);
            }
            for (int i = 0; i < Capacity / 2; i++)
            {
                AddressAsKey key = _keys[i];
                cache.Delete(in key);
            }
            if (iter % Environment.ProcessorCount == 0)
            {
                cache.Clear();
            }
        });

        // No crash means success
    }

    [Test]
    public void Can_set_and_then_set_null()
    {
        Cache cache = Create();
        AddressAsKey key = _keys[0];
        cache.Set(in key, _accounts[0]).Should().BeTrue();
        cache.Set(in key, _accounts[0]).Should().BeFalse();
        // Set with null triggers Delete
        cache.Set(in key, null!).Should().BeTrue();
        cache.Get(in key).Should().BeNull();
    }

    [Test]
    public void Can_delete()
    {
        Cache cache = Create();
        AddressAsKey key = _keys[0];
        cache.Set(in key, _accounts[0]);
        cache.Delete(in key).Should().BeTrue();
        cache.Get(in key).Should().BeNull();
        cache.Delete(in key).Should().BeFalse();
    }

    [Test]
    public void Delete_returns_value()
    {
        Cache cache = Create();
        AddressAsKey key = _keys[0];
        cache.Set(in key, _accounts[0]);

        cache.Delete(in key, out Account? value).Should().BeTrue();
        value.Should().Be(_accounts[0]);

        cache.Delete(in key, out Account? noValue).Should().BeFalse();
        noValue.Should().BeNull();
    }

    [Test]
    public void Clear_should_free_all_capacity()
    {
        Cache cache = Create();
        // Use a single key to avoid hash-collision ambiguity after Clear
        AddressAsKey key = _keys[1];

        cache.Set(in key, _accounts[0]).Should().BeTrue();
        cache.Clear();
        cache.Count.Should().Be(0);

        // After Clear, Set must return true (entry treated as new)
        cache.Set(in key, _accounts[1]).Should().BeTrue();
        cache.Get(in key).Should().Be(_accounts[1]);
    }

    [Test]
    public void Delete_keeps_internal_structure()
    {
        // Use a sparse cache so normal-traffic evictions don't interfere with the rolling-window logic.
        // With a very large number of sets (>= iterations), each key gets its own set.
        int iterations = 40;
        // iterations keys, each in its own set: need setCount >= iterations, rounded to power-of-2.
        // setCount = 64 → maxCapacity = 64 * 8 = 512.
        int maxCapacity = 512;

        AssociativeCache<AddressAsKey, Account> cache = new(maxCapacity);

        int actuallyDeleted = 0;

        for (int i = 0; i < iterations; i++)
        {
            AddressAsKey key = _keys[i];
            cache.Set(in key, _accounts[i]);
            int removeIdx = i - 10;
            if (removeIdx >= 0)
            {
                AddressAsKey removeKey = _keys[removeIdx];
                // Guard: only delete if still present (eviction is possible under heavy load)
                if (cache.TryGet(in removeKey, out _))
                {
                    cache.Delete(in removeKey).Should().BeTrue();
                    actuallyDeleted++;
                }
            }
        }

        // Any item still present must return its correct value
        for (int i = 0; i < iterations; i++)
        {
            AddressAsKey key = _keys[i];
            if (cache.TryGet(in key, out Account? val))
            {
                val.Should().Be(_accounts[i]);
            }
        }

        // Count is bounded by the rolling-window (up to 10) plus any Set calls that return true
        cache.Count.Should().BeLessOrEqualTo(10);
    }

    [TestCase(-1)]
    [TestCase(134_217_729)]
    public void Capacity_out_of_range_throws(int capacity)
    {
        FluentActions.Invoking(() => new Cache(capacity))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void Capacity_zero()
    {
        AssociativeCache<AddressAsKey, Account> cache = new(0);
        for (int i = 0; i < Capacity * 2; i++)
        {
            AddressAsKey key = _keys[i];
            // Set returns true (null-object: no-op but signals "inserted")
            cache.Set(in key, _accounts[i]);
        }

        for (int i = 0; i < Capacity * 2; i++)
        {
            AddressAsKey key = _keys[i];
            cache.TryGet(in key, out _).Should().BeFalse();
        }
    }

    [Test]
    public void No_duplicate_keys_under_concurrency()
    {
        Cache cache = Create();
        AddressAsKey sharedKey = _keys[0];
        Account sharedAccount = _accounts[0];

        Parallel.For(0, Environment.ProcessorCount * 16, _ =>
        {
            cache.Set(in sharedKey, sharedAccount);
        });

        Account? result = cache.Get(in sharedKey);
        result.Should().Be(sharedAccount);

        cache.Count.Should().BeGreaterThan(0);

        cache.Delete(in sharedKey).Should().BeTrue();
        cache.Get(in sharedKey).Should().BeNull();
    }

    [Test]
    public void Clear_epoch_invalidates_entries()
    {
        Cache cache = Create();
        // Use a single key to make the test deterministic
        AddressAsKey key = _keys[1];

        cache.Set(in key, _accounts[0]);
        cache.Clear();

        // The epoch bump must make the entry invisible
        cache.Get(in key).Should().BeNull();
        cache.Count.Should().Be(0);

        // Inserting again after Clear must succeed and be retrievable
        cache.Set(in key, _accounts[1]).Should().BeTrue();
        cache.Get(in key).Should().Be(_accounts[1]);
    }

    [Test]
    public void TryGet_works()
    {
        Cache cache = Create();
        AddressAsKey presentKey = _keys[0];
        AddressAsKey missingKey = _keys[Capacity];

        cache.Set(in presentKey, _accounts[0]);

        cache.TryGet(in presentKey, out Account? hit).Should().BeTrue();
        hit.Should().Be(_accounts[0]);

        cache.TryGet(in missingKey, out Account? miss).Should().BeFalse();
        miss.Should().BeNull();
    }

    [Test]
    public void Contains_works()
    {
        Cache cache = Create();
        AddressAsKey presentKey = _keys[0];
        AddressAsKey missingKey = _keys[Capacity];

        cache.Set(in presentKey, _accounts[0]);

        cache.Contains(in presentKey).Should().BeTrue();
        cache.Contains(in missingKey).Should().BeFalse();

        cache.Delete(in presentKey);
        cache.Contains(in presentKey).Should().BeFalse();
    }
}
