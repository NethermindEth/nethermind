// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    protected override void Clear(bool releaseReferences = true) => _cache.Clear(releaseReferences);
    protected override int GetCount() => _cache.Count;
}
