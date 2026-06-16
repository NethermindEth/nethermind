// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State.Flat.Hsst;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Hsst;

/// <summary>
/// Direct unit tests for the <see cref="UniformKeySearch"/> public helpers, targeting the
/// empty-array and length-mismatch contract arms that the format readers guard against and
/// therefore never reach in a round-trip build.
/// </summary>
[TestFixture]
public class UniformKeySearchTests
{
    // Every floor entry point returns -1 ("no stored key <= search") when there are no keys,
    // regardless of width, stride or endianness — exercises the count==0 guard arm of each.
    [Test]
    public void Floor_EmptyKeyArray_ReturnsMinusOne()
    {
        ReadOnlySpan<byte> key = stackalloc byte[8];
        ReadOnlySpan<byte> empty = default;

        Assert.That(UniformKeySearch.Uniform2LE(key, empty, 0), Is.EqualTo(-1));
        Assert.That(UniformKeySearch.Uniform4LE(key, empty, 0), Is.EqualTo(-1));
        Assert.That(UniformKeySearch.Uniform8LE(key, empty, 0), Is.EqualTo(-1));
        Assert.That(UniformKeySearch.UniformBE(key, empty, 0, keySize: 3), Is.EqualTo(-1));

        Assert.That(UniformKeySearch.Uniform2LEStrided(key, empty, 0, stride: 6), Is.EqualTo(-1));
        Assert.That(UniformKeySearch.Uniform4LEStrided(key, empty, 0, stride: 6), Is.EqualTo(-1));
        Assert.That(UniformKeySearch.Uniform8LEStrided(key, empty, 0, stride: 12), Is.EqualTo(-1));
        Assert.That(UniformKeySearch.UniformBEStrided(key, empty, 0, keySize: 3, stride: 7), Is.EqualTo(-1));
    }

    // LowerBound2LE has lower_bound semantics (smallest i with keys[i] >= target), so an empty
    // array returns 0 (the insertion point), and an all-less array returns count.
    [Test]
    public void LowerBound2LE_EmptyAndAllLess()
    {
        ReadOnlySpan<byte> target = stackalloc byte[] { 0x12, 0x34 };
        Assert.That(UniformKeySearch.LowerBound2LE(default, 0, target), Is.EqualTo(0));

        // Three LE-stored keys all numerically below 0x1234: 0x0001, 0x0002, 0x0003.
        ReadOnlySpan<byte> keys = stackalloc byte[] { 0x01, 0x00, 0x02, 0x00, 0x03, 0x00 };
        Assert.That(UniformKeySearch.LowerBound2LE(keys, 3, target), Is.EqualTo(3));
        // First key >= 0x0002 is index 1.
        ReadOnlySpan<byte> two = stackalloc byte[] { 0x00, 0x02 };
        Assert.That(UniformKeySearch.LowerBound2LE(keys, 3, two), Is.EqualTo(1));
    }

    // Keys of different byte lengths can never encode the same lex key, so StorageEqualsLex
    // short-circuits to false before inspecting any bytes — for both endianness flags.
    [Test]
    public void StorageEqualsLex_LengthMismatch_ReturnsFalse()
    {
        ReadOnlySpan<byte> stored2 = stackalloc byte[] { 0xAA, 0xBB };
        ReadOnlySpan<byte> key3 = stackalloc byte[] { 0xAA, 0xBB, 0xCC };

        Assert.That(UniformKeySearch.StorageEqualsLex(stored2, key3, isLittleEndian: false), Is.False);
        Assert.That(UniformKeySearch.StorageEqualsLex(stored2, key3, isLittleEndian: true), Is.False);

        // Sanity: equal-length keys still compare by content (BE: equal bytes; LE: reversed bytes).
        ReadOnlySpan<byte> beKey = stackalloc byte[] { 0xAA, 0xBB };
        Assert.That(UniformKeySearch.StorageEqualsLex(stored2, beKey, isLittleEndian: false), Is.True);
        ReadOnlySpan<byte> leKey = stackalloc byte[] { 0xBB, 0xAA };
        Assert.That(UniformKeySearch.StorageEqualsLex(stored2, leKey, isLittleEndian: true), Is.True);
    }
}
