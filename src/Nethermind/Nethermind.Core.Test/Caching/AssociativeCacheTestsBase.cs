// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
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
    protected virtual void AssertValue(in AddressAsKey key, int expectedIndex) => Assert.That(Get(in key), Is.True);

    [Test]
    public void At_capacity()
    {
        for (int i = 0; i < Capacity; i++)
            Assert.That(Set(in _keys[i], i), Is.True);

        AssertValue(in _keys[Capacity - 1], Capacity - 1);
    }

    [Test]
    public void Can_reset()
    {
        Assert.That(Set(in _keys[0], 0), Is.True);
        Assert.That(Set(in _keys[0], 1), Is.False);
        AssertValue(in _keys[0], 1);
    }

    [Test]
    public void Can_ask_before_first_set() => Assert.That(Get(in _keys[0]), Is.False);

    [Test]
    public void Beyond_capacity()
    {
        for (int i = 0; i < Capacity * 2; i++)
            Set(in _keys[i], i);

        Assert.That(GetCount(), Is.LessThanOrEqualTo(Capacity));

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
        Assert.That(GetCount(), Is.LessThanOrEqualTo(Capacity));
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
        Assert.That(Delete(in _keys[0]), Is.True);
        Assert.That(Get(in _keys[0]), Is.False);
        Assert.That(Delete(in _keys[0]), Is.False);
    }

    [TestCase(-1)]
    [TestCase(134_217_729)]
    public void Capacity_out_of_range_throws(int capacity) => Assert.That(() => CreateCache(capacity), Throws.TypeOf<ArgumentOutOfRangeException>());

    [TestCase(0)]
    [TestCase(4096)]
    public void Capacity_valid_boundary(int capacity)
    {
        CreateCache(capacity);
        Set(in _keys[0], 0);

        if (capacity == 0)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(Get(in _keys[0]), Is.False);
                Assert.That(GetCount(), Is.EqualTo(0));
            }
        }
        else
        {
            AssertValue(in _keys[0], 0);
            Assert.That(GetCount(), Is.EqualTo(1));
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
            Assert.That(Set(in _keys[i], i), Is.True);

        for (int i = 0; i < insertCount; i++)
            AssertValue(in _keys[i], i);

        Assert.That(GetCount(), Is.EqualTo(insertCount));
    }

    [Test]
    public void Concurrent_clear_does_not_corrupt_count()
    {
        // 50 rounds so a probabilistic Set/Clear race is caught reliably.
        for (int round = 0; round < 50; round++)
        {
            CreateCache(Capacity);
            Parallel.For(0, Environment.ProcessorCount * 4, iter =>
            {
                for (int i = 0; i < Capacity; i++)
                    Set(in _keys[i], i);
                if (iter % 3 == 0)
                    Clear();
            });

            int count = GetCount();
            Assert.That(count, Is.GreaterThanOrEqualTo(0), $"round {round}");
            Assert.That(count, Is.LessThanOrEqualTo(Capacity), $"round {round}");
        }
    }

    [Test]
    public void No_duplicate_keys_under_concurrency()
    {
        Parallel.For(0, Environment.ProcessorCount * 16, _ =>
        {
            Set(in _keys[0], 0);
        });

        AssertValue(in _keys[0], 0);
        Assert.That(GetCount(), Is.GreaterThan(0));

        Assert.That(Delete(in _keys[0]), Is.True);
        Assert.That(Get(in _keys[0]), Is.False);
    }

    [Test]
    public void Set_after_clear_persists()
    {
        Set(in _keys[0], 0);
        Clear();

        // Set immediately after Clear must succeed AND be retrievable
        Assert.That(Set(in _keys[1], 1), Is.True);
        AssertValue(in _keys[1], 1);
        Assert.That(GetCount(), Is.EqualTo(1));
    }

    [Test]
    public void Count_does_not_go_negative_on_clear_then_delete()
    {
        // Catches count underflow: Clear sets count to 0, then Delete
        // on a stale entry should not decrement below 0.
        Set(in _keys[0], 0);
        Assert.That(GetCount(), Is.EqualTo(1));

        Clear();
        Assert.That(GetCount(), Is.EqualTo(0));

        // Delete after Clear — entry is stale, delete should be a no-op
        Assert.That(Delete(in _keys[0]), Is.False);
        Assert.That(GetCount(), Is.EqualTo(0));
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

        Assert.That(GetCount(), Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public void Clear_invalidates_and_frees_capacity()
    {
        Assert.That(Set(in _keys[0], 0), Is.True);
        Clear();

        // Epoch bump makes entry invisible, count resets
        Assert.That(Get(in _keys[0]), Is.False);
        Assert.That(GetCount(), Is.EqualTo(0));

        // Capacity is free — Set returns true (new), value is retrievable
        Assert.That(Set(in _keys[0], 1), Is.True);
        AssertValue(in _keys[0], 1);
    }

    [Test]
    public void Contains_works()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Contains(in _keys[0]), Is.False);
            Assert.That(Get(in _keys[0]), Is.False);
        }

        Set(in _keys[0], 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Contains(in _keys[0]), Is.True);
            Assert.That(Get(in _keys[0]), Is.True);
        }

        Delete(in _keys[0]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Contains(in _keys[0]), Is.False);
            Assert.That(Get(in _keys[0]), Is.False);
        }
    }

    [Test]
    public void Clear_without_release_invalidates_and_allows_reuse()
    {
        // Base tests cover Clear() (releaseReferences: true). This tests the fast O(1) path.
        CreateCache(256);

        for (int i = 0; i < 16; i++)
            Set(in _keys[i], i);

        Clear(releaseReferences: false);

        Assert.That(GetCount(), Is.EqualTo(0));
        for (int i = 0; i < 16; i++)
            Assert.That(Get(in _keys[i]), Is.False);

        // Re-insert — all should report as new
        for (int i = 0; i < 16; i++)
            Assert.That(Set(in _keys[i], i), Is.True, $"key {i} should be new after Clear");

        Assert.That(GetCount(), Is.EqualTo(16));
    }
}
