// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Caching;
using NUnit.Framework;

using Cache = Nethermind.Core.Caching.AssociativeCache<Nethermind.Core.AddressAsKey, Nethermind.Core.Account>;

namespace Nethermind.Core.Test.Caching;

[TestFixture]
public class AssociativeCacheTests : AssociativeCacheTestsBase
{
    private Cache _cache = null!;

    protected override void CreateCache(int capacity) => _cache = new Cache(capacity);
    protected override bool Set(in AddressAsKey key, int accountIndex) => _cache.Set(in key, _accounts[accountIndex]);
    protected override bool Get(in AddressAsKey key) => _cache.Get(in key) is not null;
    protected override bool Contains(in AddressAsKey key) => _cache.Contains(in key);
    protected override bool Delete(in AddressAsKey key) => _cache.Delete(in key);
    protected override void Clear() => _cache.Clear();
    protected override int GetCount() => _cache.Count;

    protected override void AssertValue(in AddressAsKey key, int expectedIndex)
    {
        _cache.TryGet(in key, out Account? val).Should().BeTrue("key should be present");
        val.Should().Be(_accounts[expectedIndex]);
    }

    [Test]
    public void Can_set_and_then_set_null()
    {
        AddressAsKey key = _keys[0];
        _cache.Set(in key, _accounts[0]).Should().BeTrue();
        _cache.Set(in key, _accounts[0]).Should().BeFalse();
        // Set with null triggers Delete
        _cache.Set(in key, null!).Should().BeTrue();
        _cache.Get(in key).Should().BeNull();
    }

    [Test]
    public void Delete_returns_value()
    {
        AddressAsKey key = _keys[0];
        _cache.Set(in key, _accounts[0]);

        _cache.Delete(in key, out Account? value).Should().BeTrue();
        value.Should().Be(_accounts[0]);

        _cache.Delete(in key, out Account? noValue).Should().BeFalse();
        noValue.Should().BeNull();
    }

    [Test]
    public void Delete_keeps_internal_structure()
    {
        // Use a sparse cache so normal-traffic evictions don't interfere with the rolling-window logic.
        // With a very large number of sets (>= iterations), each key gets its own set.
        int iterations = 40;
        // iterations keys, each in its own set: need setCount >= iterations, rounded to power-of-2.
        // setCount = 64 -> maxCapacity = 64 * 8 = 512.
        int maxCapacity = 512;

        AssociativeCache<AddressAsKey, Account> cache = new(maxCapacity);

        for (int i = 0; i < iterations; i++)
        {
            AddressAsKey key = _keys[i];
            cache.Set(in key, _accounts[i]);
            int removeIdx = i - 10;
            if (removeIdx >= 0)
            {
                AddressAsKey removeKey = _keys[removeIdx];
                if (cache.TryGet(in removeKey, out _))
                {
                    cache.Delete(in removeKey).Should().BeTrue();
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

    [TestCase(true)]
    [TestCase(false)]
    public void Clear_both_modes_invalidate_entries(bool releaseReferences)
    {
        for (int i = 0; i < Capacity; i++)
            _cache.Set(in _keys[i], _accounts[i]);

        _cache.Clear(releaseReferences);

        _cache.Count.Should().Be(0);
        for (int i = 0; i < Capacity; i++)
            _cache.Get(in _keys[i]).Should().BeNull();
    }

    [Test]
    public void Clear_with_release_preserves_entries_written_after_epoch_bump()
    {
        // Entries written after the epoch bump must not be nulled by the reference release pass.
        Cache cache = new(256);

        for (int i = 0; i < Capacity; i++)
            cache.Set(in _keys[i], _accounts[i]);

        cache.Clear();

        // Insert after clear
        for (int i = 0; i < 16; i++)
            cache.Set(in _keys[i], _accounts[i]);

        for (int i = 0; i < 16; i++)
        {
            AddressAsKey key = _keys[i];
            cache.TryGet(in key, out Account? val).Should().BeTrue($"key {i} should survive Clear's release pass");
            val.Should().Be(_accounts[i]);
        }

        cache.Count.Should().Be(16);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Clear_both_modes_allow_reuse(bool releaseReferences)
    {
        // Use a large cache so 16 inserts stay well below per-set limits (no eviction).
        Cache cache = new(256);
        int insertCount = 16;

        for (int i = 0; i < insertCount; i++)
            cache.Set(in _keys[i], _accounts[i]);

        cache.Clear(releaseReferences);

        // Re-insert — all should report as new
        for (int i = 0; i < insertCount; i++)
            cache.Set(in _keys[i], _accounts[i]).Should().BeTrue($"key {i} should be new after Clear");

        cache.Count.Should().Be(insertCount);
    }

    [Test]
    public void Clear_mixed_modes_parallel()
    {
        Cache cache = new(256);

        Parallel.For(0, Environment.ProcessorCount * 4, iter =>
        {
            for (int i = 0; i < 64; i++)
                cache.Set(in _keys[i], _accounts[i]);
            if (iter % 2 == 0)
                cache.Clear(releaseReferences: true);
            else
                cache.Clear(releaseReferences: false);
            for (int i = 0; i < 64; i++)
                cache.Set(in _keys[i], _accounts[i]);
        });

        cache.Count.Should().BeGreaterThanOrEqualTo(0);
        cache.Count.Should().BeLessOrEqualTo(256);
    }

    [Test]
    public void TryGet_returns_value()
    {
        AddressAsKey presentKey = _keys[0];
        AddressAsKey missingKey = _keys[Capacity];

        _cache.Set(in presentKey, _accounts[0]);

        _cache.TryGet(in presentKey, out Account? hit).Should().BeTrue();
        hit.Should().Be(_accounts[0]);

        _cache.TryGet(in missingKey, out Account? miss).Should().BeFalse();
        miss.Should().BeNull();

        _cache.Delete(in presentKey);
        _cache.TryGet(in presentKey, out _).Should().BeFalse();
    }
}
