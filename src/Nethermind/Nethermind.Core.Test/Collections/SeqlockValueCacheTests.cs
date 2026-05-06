// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using FluentAssertions;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Core.Test.Collections;

public class SeqlockValueCacheTests
{
    private readonly record struct Bound(long Offset, long Length);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct IntKey(int id) : IHash64bit<IntKey>, IEquatable<IntKey>
    {
        public readonly int Id = id;
        public long GetHashCode64() => Id * unchecked((long)0x9E37_79B9_7F4A_7C15);
        public bool Equals(in IntKey other) => Id == other.Id;
        public bool Equals(IntKey other) => Id == other.Id;
        public override bool Equals(object? obj) => obj is IntKey k && Equals(k);
        public override int GetHashCode() => Id;
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(3)]
    [TestCase(7)]
    [TestCase(100)]
    public void Ctor_rejects_non_power_of_two(int sets)
    {
        Action act = () => new SeqlockValueCache<IntKey, Bound>(sets);
        act.Should().Throw<ArgumentException>();
    }

    [TestCase(1)]
    [TestCase(2)]
    [TestCase(8)]
    [TestCase(1024)]
    public void Ctor_accepts_powers_of_two(int sets)
    {
        Action act = () => new SeqlockValueCache<IntKey, Bound>(sets);
        act.Should().NotThrow();
    }

    [Test]
    public void New_cache_returns_miss()
    {
        SeqlockValueCache<IntKey, Bound> cache = new(8);
        IntKey key = new(1);

        bool found = cache.TryGetValue(in key, out Bound value);

        found.Should().BeFalse();
        value.Should().Be(default(Bound));
    }

    [Test]
    public void Set_then_get_round_trips_value()
    {
        SeqlockValueCache<IntKey, Bound> cache = new(8);
        IntKey key = new(42);
        Bound expected = new(123, 456);

        cache.Set(in key, expected);
        bool found = cache.TryGetValue(in key, out Bound value);

        found.Should().BeTrue();
        value.Should().Be(expected);
    }

    [Test]
    public void Set_overwrites_existing_value()
    {
        SeqlockValueCache<IntKey, Bound> cache = new(8);
        IntKey key = new(1);

        cache.Set(in key, new Bound(1, 1));
        cache.Set(in key, new Bound(99, 100));

        cache.TryGetValue(in key, out Bound value).Should().BeTrue();
        value.Should().Be(new Bound(99, 100));
    }

    [Test]
    public void Multiple_distinct_keys_are_kept_independently()
    {
        SeqlockValueCache<IntKey, Bound> cache = new(64);
        for (int i = 0; i < 32; i++)
        {
            IntKey k = new(i);
            cache.Set(in k, new Bound(i * 10, i + 1));
        }

        for (int i = 0; i < 32; i++)
        {
            IntKey k = new(i);
            cache.TryGetValue(in k, out Bound v).Should().BeTrue($"key {i}");
            v.Should().Be(new Bound(i * 10, i + 1));
        }
    }

    [Test]
    public void Clear_logically_empties_cache()
    {
        SeqlockValueCache<IntKey, Bound> cache = new(8);
        IntKey key = new(1);
        cache.Set(in key, new Bound(7, 8));
        cache.TryGetValue(in key, out _).Should().BeTrue();

        cache.Clear();

        cache.TryGetValue(in key, out Bound v).Should().BeFalse();
        v.Should().Be(default(Bound));
    }

    [Test]
    public void GetOrAdd_invokes_factory_on_miss_and_caches()
    {
        SeqlockValueCache<IntKey, Bound> cache = new(8);
        IntKey key = new(7);
        int calls = 0;

        Bound first = cache.GetOrAdd(in key, (in IntKey k) => { calls++; return new Bound(k.Id, k.Id * 2); });
        Bound second = cache.GetOrAdd(in key, (in IntKey k) => { calls++; return new Bound(-1, -1); });

        first.Should().Be(new Bound(7, 14));
        second.Should().Be(new Bound(7, 14));
        calls.Should().Be(1);
    }

    [Test]
    public void Works_with_ValueHash256_and_Bound()
    {
        SeqlockValueCache<ValueHash256, Bound> cache = new(8);
        ValueHash256 key = Keccak.Compute("addr-test").ValueHash256;
        Bound bound = new(0xCAFE_BABE, 0xDEAD_BEEF);

        cache.Set(in key, bound);

        cache.TryGetValue(in key, out Bound got).Should().BeTrue();
        got.Should().Be(bound);
    }
}
