// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat.Collections;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Collections;

public class LargeArrayPoolTests
{
    private static LargeArrayPool<long> CreatePool() => new(minLength: 1024, maxLength: 8192, maxArraysPerLength: 2, trimOnGen2: false);

    [TestCase(1, 1024)]
    [TestCase(1024, 1024)]
    [TestCase(1025, 2048)]
    [TestCase(8192, 8192)]
    public void Rent_returns_the_power_of_two_length_class(int requested, int expectedLength) =>
        Assert.That(CreatePool().Rent(requested), Has.Length.EqualTo(expectedLength));

    [Test]
    public void Rent_beyond_the_largest_class_is_not_pooled()
    {
        LargeArrayPool<long> pool = CreatePool();
        long[] array = pool.Rent(10_000);
        pool.Return(array);

        Assert.That(array, Has.Length.EqualTo(10_000));
        Assert.That(pool.Rent(10_000), Is.Not.SameAs(array));
    }

    [Test]
    public void Returned_array_is_rented_again_and_capacity_is_bounded()
    {
        LargeArrayPool<long> pool = CreatePool();
        long[] first = pool.Rent(2048), second = pool.Rent(2048), third = pool.Rent(2048);
        pool.Return(first);
        pool.Return(second);
        pool.Return(third);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.Rent(2048), Is.SameAs(second));
            Assert.That(pool.Rent(2048), Is.SameAs(first));
            Assert.That(pool.Rent(2048), Is.Not.SameAs(third));
        }
    }

    [Test]
    public void Array_of_a_foreign_length_is_not_kept()
    {
        LargeArrayPool<long> pool = CreatePool();
        long[] foreign = new long[1500];
        pool.Return(foreign);

        Assert.That(pool.Rent(1500), Is.Not.SameAs(foreign));
    }

    [TestCase(false, false, true, TestName = "Trim_keeps_arrays_used_since_the_previous_trim")]
    [TestCase(false, true, false, TestName = "Trim_releases_arrays_idle_since_the_previous_trim")]
    [TestCase(true, false, false, TestName = "Trim_under_memory_pressure_releases_everything")]
    public void Trim_releases_idle_or_all_arrays(bool all, bool idleRound, bool kept)
    {
        LargeArrayPool<long> pool = CreatePool();
        long[] array = pool.Rent(4096);
        pool.Return(array);
        if (idleRound) pool.Trim(all: false);

        pool.Trim(all);

        Assert.That(pool.Rent(4096) == array, Is.EqualTo(kept));
    }
}
