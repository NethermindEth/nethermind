// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class StaticCodeCacheTests
{
    [TestCase(StaticCodeCache.SmallCodeSize)]
    [TestCase(StaticCodeCache.MediumCodeSize)]
    public void A_flood_of_maximum_size_contracts_does_not_evict_the_code_ordinary_blocks_run(int hotCodeLength)
    {
        StaticCodeCache cache = new(smallCapacity: 64, mediumCapacity: 64, largeCapacity: 64);
        ValueHash256 hot = Hash(0);
        CodeInfo hotCode = new(new byte[hotCodeLength]);
        cache.Set(in hot, hotCode);

        Flood(cache, StaticCodeCache.MediumCodeSize + 1, count: 10_000);

        Assert.That(cache.Get(in hot), Is.SameAs(hotCode));
    }

    [Test]
    public void One_tier_is_evicted_by_the_same_flood()
    {
        StaticCodeCache cache = new(maxCapacity: 192);
        ValueHash256 hot = Hash(0);
        cache.Set(in hot, new CodeInfo(new byte[StaticCodeCache.SmallCodeSize]));

        Flood(cache, StaticCodeCache.MediumCodeSize + 1, count: 10_000);

        Assert.That(cache.Get(in hot), Is.Null);
    }

    [TestCase(1)]
    [TestCase(StaticCodeCache.SmallCodeSize + 1)]
    [TestCase(StaticCodeCache.MediumCodeSize + 1)]
    public void Code_of_every_size_is_found_and_cleared(int codeLength)
    {
        StaticCodeCache cache = new(smallCapacity: 64, mediumCapacity: 64, largeCapacity: 64);
        ValueHash256 hash = Hash(0);
        CodeInfo code = new(new byte[codeLength]);
        cache.Set(in hash, code);

        Assert.That(cache.Get(in hash), Is.SameAs(code));
        cache.Clear();
        Assert.That(cache.Get(in hash), Is.Null);
    }

    private static void Flood(StaticCodeCache cache, int codeLength, int count)
    {
        ReadOnlyMemory<byte> code = new byte[codeLength];
        for (int i = 1; i <= count; i++)
        {
            ValueHash256 hash = Hash(i);
            cache.Set(in hash, new CodeInfo(code));
        }
    }

    private static ValueHash256 Hash(int i) => ValueKeccak.Compute(BitConverter.GetBytes(i));
}
