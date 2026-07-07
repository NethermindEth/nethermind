// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Trie.Test;

[Parallelizable(ParallelScope.All)]
public class NodeStorageCacheTests
{
    [Test]
    public void GetOrAdd_uses_factory_every_time_when_disabled()
    {
        NodeStorageCache cache = new();
        NodeKey key = new(null, TreePath.Empty, TestItem.KeccakA);
        int calls = 0;
        SeqlockCache<NodeKey, byte[]>.ValueFactory factory = (in NodeKey _) => [(byte)++calls];

        byte[]? first = cache.GetOrAdd(in key, factory);
        byte[]? second = cache.GetOrAdd(in key, factory);

        Assert.That(calls, Is.EqualTo(2));
        Assert.That(first, Is.Not.SameAs(second));
    }

    [Test]
    public void GetOrAdd_reuses_value_when_enabled()
    {
        NodeStorageCache cache = new() { Enabled = true };
        NodeKey key = new(null, TreePath.Empty, TestItem.KeccakA);
        int calls = 0;
        SeqlockCache<NodeKey, byte[]>.ValueFactory factory = (in NodeKey _) => [(byte)++calls];

        byte[]? first = cache.GetOrAdd(in key, factory);
        byte[]? second = cache.GetOrAdd(in key, factory);

        Assert.That(calls, Is.EqualTo(1));
        Assert.That(second, Is.SameAs(first));
    }

    [Test]
    public void ClearCaches_disables_and_invalidates_cache()
    {
        NodeStorageCache cache = new() { Enabled = true };
        NodeKey key = new(null, TreePath.Empty, TestItem.KeccakA);
        int calls = 0;
        SeqlockCache<NodeKey, byte[]>.ValueFactory factory = (in NodeKey _) => [(byte)++calls];

        byte[]? first = cache.GetOrAdd(in key, factory);

        Assert.That(cache.ClearCaches(), Is.True);
        Assert.That(cache.Enabled, Is.False);

        cache.Enabled = true;
        byte[]? second = cache.GetOrAdd(in key, factory);

        Assert.That(calls, Is.EqualTo(2));
        Assert.That(second, Is.Not.SameAs(first));
    }

    [Test]
    public void ClearCaches_reports_when_cache_was_disabled()
    {
        NodeStorageCache cache = new();

        Assert.That(cache.ClearCaches(), Is.False);
    }

    [TestCase(0)]
    [TestCase(SeqlockCache<NodeKey, byte[]>.MaxSetsBits + 1)]
    public void Constructor_rejects_invalid_cache_size(int cacheSetsBits)
        => Assert.Throws<ArgumentOutOfRangeException>(() => _ = new NodeStorageCache(new NodeStorageCacheConfig { CacheSetsBits = cacheSetsBits }));
}
