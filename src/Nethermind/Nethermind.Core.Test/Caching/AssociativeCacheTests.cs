// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
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
    protected override void Clear(bool releaseReferences = true) => _cache.Clear(releaseReferences);
    protected override int GetCount() => _cache.Count;

    protected override void AssertValue(in AddressAsKey key, int expectedIndex)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_cache.TryGet(in key, out Account? val), Is.True, "key should be present");
            Assert.That(val, Is.EqualTo(_accounts[expectedIndex]));
        }
    }

    [Test]
    public void Can_set_and_then_set_null()
    {
        AddressAsKey key = _keys[0];
        Assert.That(_cache.Set(in key, _accounts[0]), Is.True);
        Assert.That(_cache.Set(in key, _accounts[0]), Is.False);
        // Set with null triggers Delete
        Assert.That(_cache.Set(in key, null!), Is.True);
        Assert.That(_cache.Get(in key), Is.Null);
    }

    [Test]
    public void Delete_returns_value()
    {
        AddressAsKey key = _keys[0];
        _cache.Set(in key, _accounts[0]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_cache.Delete(in key, out Account? value), Is.True);
            Assert.That(value, Is.EqualTo(_accounts[0]));
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_cache.Delete(in key, out Account? noValue), Is.False);
            Assert.That(noValue, Is.Null);
        }
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
                    Assert.That(cache.Delete(in removeKey), Is.True);
                }
            }
        }

        // Any item still present must return its correct value
        for (int i = 0; i < iterations; i++)
        {
            AddressAsKey key = _keys[i];
            if (cache.TryGet(in key, out Account? val))
            {
                Assert.That(val, Is.EqualTo(_accounts[i]));
            }
        }

        // Count is bounded by the rolling-window (up to 10) plus any Set calls that return true
        Assert.That(cache.Count, Is.LessThanOrEqualTo(10));
    }

    [Test]
    public void Clear_with_release_preserves_entries_written_after_epoch_bump()
    {
        // Race the O(n) release pass against concurrent inserts.
        // Entries written after the epoch bump must survive the null pass.
        Cache cache = new(256);

        for (int i = 0; i < Capacity; i++)
            cache.Set(in _keys[i], _accounts[i]);

        Parallel.Invoke(
            () => cache.Clear(),
            () =>
            {
                // Insert while ClearEntries is scanning sets
                for (int i = 0; i < 16; i++)
                    cache.Set(in _keys[i], _accounts[i]);
            }
        );

        // Entries that survived must have correct values
        for (int i = 0; i < 16; i++)
        {
            AddressAsKey key = _keys[i];
            if (cache.TryGet(in key, out Account? val))
                Assert.That(val, Is.EqualTo(_accounts[i]));
        }
    }

    [Test]
    public void TryGet_returns_value()
    {
        AddressAsKey presentKey = _keys[0];
        AddressAsKey missingKey = _keys[Capacity];

        _cache.Set(in presentKey, _accounts[0]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_cache.TryGet(in presentKey, out Account? hit), Is.True);
            Assert.That(hit, Is.EqualTo(_accounts[0]));

            Assert.That(_cache.TryGet(in missingKey, out Account? miss), Is.False);
            Assert.That(miss, Is.Null);
        }

        _cache.Delete(in presentKey);
        Assert.That(_cache.TryGet(in presentKey, out _), Is.False);
    }
}
