// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Caching;
using NUnit.Framework;

namespace Nethermind.Core.Test.Caching;

[TestFixture]
public class AssociativeKeyCacheTests : AssociativeCacheTestsBase
{
    private AssociativeKeyCache<AddressAsKey> _cache = null!;

    protected override void CreateCache(int capacity) => _cache = new AssociativeKeyCache<AddressAsKey>(capacity);
    protected override bool Set(in AddressAsKey key, int accountIndex) => _cache.Set(in key);
    protected override bool Get(in AddressAsKey key) => _cache.Get(in key);
    protected override bool Contains(in AddressAsKey key) => _cache.Contains(in key);
    protected override bool Delete(in AddressAsKey key) => _cache.Delete(in key);
    protected override void Clear() => _cache.Clear();
    protected override int GetCount() => _cache.Count;

    [Test]
    public void Contains_works()
    {
        _cache.Contains(in _keys[0]).Should().BeFalse();
        _cache.Get(in _keys[0]).Should().BeFalse();

        _cache.Set(in _keys[0]);

        _cache.Contains(in _keys[0]).Should().BeTrue();
        _cache.Get(in _keys[0]).Should().BeTrue();

        _cache.Delete(in _keys[0]);

        _cache.Contains(in _keys[0]).Should().BeFalse();
        _cache.Get(in _keys[0]).Should().BeFalse();
    }
}
