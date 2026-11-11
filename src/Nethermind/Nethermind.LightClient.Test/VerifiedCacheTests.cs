// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.LightClient.Test;

public class VerifiedCacheTests
{
    [Test]
    public void Capacity_must_be_positive([Values(-1, 0)] long capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new VerifiedMemoryCache(capacity));

    [Test]
    public void Verified_cache_admits_large_entries_after_warming_and_evicts_least_recently_used(
        [Values(1024L, VerifiedMemoryCache.DefaultCapacity)] long capacity)
    {
        using VerifiedMemoryCache cache = new(capacity);
        for (int i = 0; i < 32; i++) cache.Set(i, i, capacity / 32);
        Assert.That(cache.TryGetValue(0, out int first), Is.True);
        Assert.That(first, Is.Zero);
        cache.Set("large", 42, capacity / 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryGetValue(0, out int retained), Is.True);
            Assert.That(retained, Is.Zero);
            Assert.That(cache.TryGetValue(1, out int _), Is.False);
            Assert.That(cache.TryGetValue("large", out int large), Is.True);
            Assert.That(large, Is.EqualTo(42));
        }
        cache.Set("large", 43, capacity);
        cache.Set("oversized", 99, capacity + 1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.TryGetValue(0, out int _), Is.False);
            Assert.That(cache.TryGetValue("large", out int replaced), Is.True);
            Assert.That(replaced, Is.EqualTo(43));
            Assert.That(cache.TryGetValue("oversized", out int _), Is.False);
        }
    }

    [Test]
    public async Task Payload_eviction_refetches_only_the_least_recently_used_value()
    {
        await using VerifiedPayloadCache<int> cache = new(static _ => 1024, CancellationToken.None, cacheCapacity: 2048);
        Hash256 first = Keccak.Compute("first");
        Hash256 second = Keccak.Compute("second");
        Hash256 third = Keccak.Compute("third");
        Assert.That(await cache.GetAsync(first, _ => Task.FromResult(11), CancellationToken.None), Is.EqualTo(11));
        Assert.That(await cache.GetAsync(second, _ => Task.FromResult(22), CancellationToken.None), Is.EqualTo(22));
        Assert.That(await cache.GetAsync(first, _ => throw new AssertionException("The first entry must still be cached."), CancellationToken.None), Is.EqualTo(11));
        Assert.That(await cache.GetAsync(third, _ => Task.FromResult(33), CancellationToken.None), Is.EqualTo(33));
        Assert.That(await cache.GetAsync(first, _ => throw new AssertionException("Reading the first entry must protect it from eviction."), CancellationToken.None), Is.EqualTo(11));
        Assert.That(await cache.GetAsync(second, _ => Task.FromResult(23), CancellationToken.None), Is.EqualTo(23));
    }
}
