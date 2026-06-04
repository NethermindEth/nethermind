// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

/// <summary>
/// Pins the ordinal-keyed prefetch destination: a slot reads back only after it is set, a loaded
/// zero (null/empty) is distinct from "not loaded", and unset slots report not-loaded.
/// </summary>
[TestFixture]
public class BalStorageValueCacheTests
{
    [Test]
    public void Unset_ordinal_reports_not_loaded()
    {
        using BalStorageValueCache cache = new(4);

        Assert.That(cache.Count, Is.EqualTo(4));
        Assert.That(cache.TryGet(0, out byte[]? value), Is.False);
        Assert.That(value, Is.Null);
    }

    [Test]
    public void Set_value_is_read_back_without_affecting_other_ordinals()
    {
        using BalStorageValueCache cache = new(4);
        byte[] payload = [1, 2, 3];

        cache.Set(2, payload);

        Assert.That(cache.TryGet(2, out byte[]? value), Is.True);
        Assert.That(value, Is.SameAs(payload));

        Assert.That(cache.TryGet(0, out _), Is.False);
        Assert.That(cache.TryGet(3, out _), Is.False);
    }

    [Test]
    public void Loaded_zero_is_ready_and_distinct_from_not_loaded()
    {
        using BalStorageValueCache cache = new(2);

        cache.Set(1, null); // known-zero slot

        Assert.That(cache.TryGet(1, out byte[]? zero), Is.True);
        Assert.That(zero, Is.Null);

        Assert.That(cache.TryGet(0, out _), Is.False); // never set -> still not loaded
    }

    [Test]
    public void Zero_count_cache_is_usable_and_disposes()
    {
        using BalStorageValueCache cache = new(0);
        Assert.That(cache.Count, Is.EqualTo(0));
    }
}
