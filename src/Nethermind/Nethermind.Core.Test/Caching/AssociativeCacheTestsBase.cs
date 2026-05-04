// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace Nethermind.Core.Test.Caching;

/// <summary>
/// Shared contract tests for <see cref="Caching.AssociativeCache{TKey,TValue}"/>
/// and <see cref="Caching.AssociativeKeyCache{TKey}"/>.
/// Each derived class wires the abstract operations to its concrete cache type.
/// </summary>
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
[Parallelizable(ParallelScope.All)]
public abstract class AssociativeCacheTestsBase
{
    protected const int Capacity = 32;

    protected AddressAsKey[] _keys = null!;
    protected Account[] _accounts = null!;

    [SetUp]
    public void Setup()
    {
        (_keys, _accounts) = CacheTestData.Build(Capacity * 2 + 1);
        CreateCache(Capacity);
    }

    protected abstract void CreateCache(int capacity);

    /// <summary>Set key with associated account index. Returns true if new insert.</summary>
    protected abstract bool Set(in AddressAsKey key, int accountIndex);

    /// <summary>Returns true if key is present.</summary>
    protected abstract bool Get(in AddressAsKey key);

    protected abstract bool Contains(in AddressAsKey key);
    protected abstract bool Delete(in AddressAsKey key);
    protected abstract void Clear(bool releaseReferences = true);
    protected abstract int GetCount();

    /// <summary>
    /// Asserts that <paramref name="key"/> is present and maps to <c>_accounts[expectedIndex]</c>.
    /// Key-only caches just assert presence.
    /// </summary>
    protected virtual void AssertValue(in AddressAsKey key, int expectedIndex) => Get(in key).Should().BeTrue();

    [Test]
    public void At_capacity()
    {
        for (int i = 0; i < Capacity; i++)
            Set(in _keys[i], i).Should().BeTrue();

        AssertValue(in _keys[Capacity - 1], Capacity - 1);
    }

    [Test]
    public void Can_reset()
    {
        Set(in _keys[0], 0).Should().BeTrue();
        Set(in _keys[0], 1).Should().BeFalse();
        AssertValue(in _keys[0], 1);
    }

    [Test]
    public void Can_ask_before_first_set() => Get(in _keys[0]).Should().BeFalse();

    [Test]
    public void Beyond_capacity()
    {
        for (int i = 0; i < Capacity * 2; i++)
            Set(in _keys[i], i);

        GetCount().Should().BeLessOrEqualTo(Capacity);

        // Any item that is present must return the correct value
        for (int i = 0; i < Capacity * 2; i++)
        {
            if (Get(in _keys[i]))
                AssertValue(in _keys[i], i);
        }
    }

    [Test]
    public void Beyond_capacity_stress()
    {
        for (int iter = 0; iter < 4; iter++)
        {
            for (int i = 0; i < Capacity * 2; i++)
                Set(in _keys[i], i);
            for (int i = 0; i < Capacity * 2; i++)
                Get(in _keys[i]);
            if (iter % 2 == 0)
                Clear();
        }

        // No crash means success; count is bounded
        GetCount().Should().BeLessOrEqualTo(Capacity);
    }

    [Test]
    public void Beyond_capacity_parallel() => Parallel.For(0, Environment.ProcessorCount * 8, iter =>
    {
        for (int i = 0; i < Capacity * 2; i++)
            Set(in _keys[i], i);
        for (int i = 0; i < Capacity * 2; i++)
            Get(in _keys[i]);
        for (int i = 0; i < Capacity / 2; i++)
            Delete(in _keys[i]);
        if (iter % Environment.ProcessorCount == 0)
            Clear();
    });// No crash means success

    [Test]
    public void Can_delete()
    {
        Set(in _keys[0], 0);
        Delete(in _keys[0]).Should().BeTrue();
        Get(in _keys[0]).Should().BeFalse();
        Delete(in _keys[0]).Should().BeFalse();
    }

    [TestCase(-1)]
    [TestCase(134_217_729)]
    public void Capacity_out_of_range_throws(int capacity) => FluentActions.Invoking(() => CreateCache(capacity))
            .Should().Throw<ArgumentOutOfRangeException>();

    [TestCase(0)]
    [TestCase(4096)]
    public void Capacity_valid_boundary(int capacity)
    {
        CreateCache(capacity);
        Set(in _keys[0], 0);

        if (capacity == 0)
        {
            Get(in _keys[0]).Should().BeFalse();
            GetCount().Should().Be(0);
        }
        else
        {
            AssertValue(in _keys[0], 0);
            GetCount().Should().Be(1);
        }
    }

    [TestCase(8)]
    [TestCase(32)]
    [TestCase(256)]
    [TestCase(1024)]
    public void All_inserted_keys_retrievable_at_various_capacities(int capacity)
    {
        // Catches hash signature extraction bugs: if the signature is computed from
        // wrong bits, Get won't find keys that were just inserted (tag mismatch).
        // Insert only 50% of capacity to avoid set-conflict eviction.
        CreateCache(capacity);
        int insertCount = Math.Min(capacity / 2, _keys.Length - 1);

        for (int i = 0; i < insertCount; i++)
            Set(in _keys[i], i).Should().BeTrue();

        for (int i = 0; i < insertCount; i++)
            AssertValue(in _keys[i], i);

        GetCount().Should().Be(insertCount);
    }

    [Test]
    public void Concurrent_clear_does_not_corrupt_count()
    {
        // Catches count/Clear race: concurrent Set + Clear should not produce
        // negative counts or counts wildly exceeding capacity.
        Parallel.For(0, Environment.ProcessorCount * 4, iter =>
        {
            for (int i = 0; i < Capacity; i++)
                Set(in _keys[i], i);
            if (iter % 3 == 0)
                Clear();
        });

        int count = GetCount();
        count.Should().BeGreaterThanOrEqualTo(0);
        count.Should().BeLessOrEqualTo(Capacity);
    }

    [Test]
    public void No_duplicate_keys_under_concurrency()
    {
        Parallel.For(0, Environment.ProcessorCount * 16, _ =>
        {
            Set(in _keys[0], 0);
        });

        AssertValue(in _keys[0], 0);
        GetCount().Should().BeGreaterThan(0);

        Delete(in _keys[0]).Should().BeTrue();
        Get(in _keys[0]).Should().BeFalse();
    }

    [Test]
    public void Set_after_clear_persists()
    {
        Set(in _keys[0], 0);
        Clear();

        // Set immediately after Clear must succeed AND be retrievable
        Set(in _keys[1], 1).Should().BeTrue();
        AssertValue(in _keys[1], 1);
        GetCount().Should().Be(1);
    }

    [Test]
    public void Count_does_not_go_negative_on_clear_then_delete()
    {
        // Catches count underflow: Clear sets count to 0, then Delete
        // on a stale entry should not decrement below 0.
        Set(in _keys[0], 0);
        GetCount().Should().Be(1);

        Clear();
        GetCount().Should().Be(0);

        // Delete after Clear — entry is stale, delete should be a no-op
        Delete(in _keys[0]).Should().BeFalse();
        GetCount().Should().Be(0);
    }

    [Test]
    public void Concurrent_delete_and_clear_keeps_count_non_negative()
    {
        // Stress test: concurrent Delete + Clear should never produce negative count.
        CreateCache(256);

        Parallel.For(0, Environment.ProcessorCount * 4, iter =>
        {
            for (int i = 0; i < 64; i++)
                Set(in _keys[i], i);
            if (iter % 2 == 0)
                Clear();
            for (int i = 0; i < 64; i++)
                Delete(in _keys[i]);
        });

        GetCount().Should().BeGreaterThanOrEqualTo(0);
    }

    [Test]
    public void Clear_invalidates_and_frees_capacity()
    {
        Set(in _keys[0], 0).Should().BeTrue();
        Clear();

        // Epoch bump makes entry invisible, count resets
        Get(in _keys[0]).Should().BeFalse();
        GetCount().Should().Be(0);

        // Capacity is free — Set returns true (new), value is retrievable
        Set(in _keys[0], 1).Should().BeTrue();
        AssertValue(in _keys[0], 1);
    }

    [Test]
    public void Contains_works()
    {
        Contains(in _keys[0]).Should().BeFalse();
        Get(in _keys[0]).Should().BeFalse();

        Set(in _keys[0], 0);

        Contains(in _keys[0]).Should().BeTrue();
        Get(in _keys[0]).Should().BeTrue();

        Delete(in _keys[0]);

        Contains(in _keys[0]).Should().BeFalse();
        Get(in _keys[0]).Should().BeFalse();
    }

    [Test]
    public void Clear_without_release_invalidates_and_allows_reuse()
    {
        // Base tests cover Clear() (releaseReferences: true). This tests the fast O(1) path.
        CreateCache(256);

        for (int i = 0; i < 16; i++)
            Set(in _keys[i], i);

        Clear(releaseReferences: false);

        GetCount().Should().Be(0);
        for (int i = 0; i < 16; i++)
            Get(in _keys[i]).Should().BeFalse();

        // Re-insert — all should report as new
        for (int i = 0; i < 16; i++)
            Set(in _keys[i], i).Should().BeTrue($"key {i} should be new after Clear");

        GetCount().Should().Be(16);
    }
}
