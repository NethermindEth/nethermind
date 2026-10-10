// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Evm.CodeAnalysis;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

[TestFixture]
public class BlockCodeCacheTests
{
    private const int LargeCodeLength = StaticCodeCache.MediumCodeSize + 1;
    private const long LargeCodeCharge = LargeCodeLength + (LargeCodeLength >> 3) + BlockCodeCache.EntryOverheadBytes;

    [Test]
    public void A_cycle_over_more_code_than_the_process_wide_cache_holds_is_loaded_once()
    {
        BlockCodeCache cache = new(new StaticCodeCache(smallCapacity: 64, mediumCapacity: 64, largeCapacity: 64));
        CodeInfo[] codes = Load(cache, count: 1_000);

        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < codes.Length; i++)
            {
                ValueHash256 hash = Hash(i);
                Assert.That(cache.Get(in hash), Is.SameAs(codes[i]), $"round {round}, code {i}");
            }
        }
    }

    [Test]
    public void Code_past_the_cap_is_not_kept_and_does_not_evict_kept_code()
    {
        BlockCodeCache cache = new(NoopCodeCache.Instance, maxBytes: 10 * LargeCodeCharge);
        CodeInfo[] codes = Load(cache, count: 20);

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < codes.Length; i++)
            {
                ValueHash256 hash = Hash(i);
                Assert.That(cache.Get(in hash), i < 10 ? Is.SameAs(codes[i]) : Is.Null, $"code {i}");
            }

            Assert.That(cache.Bytes, Is.EqualTo(10 * LargeCodeCharge));
        }
    }

    [Test]
    public void A_limited_view_shares_the_block_but_stops_taking_code_in_at_its_own_limit()
    {
        BlockCodeCache cache = new(NoopCodeCache.Instance, maxBytes: 10 * LargeCodeCharge);
        BlockCodeCache view = cache.WithLimit(4 * LargeCodeCharge);
        CodeInfo[] warmed = Load(view, count: 6);
        CodeInfo executed = new(new byte[LargeCodeLength]);
        ValueHash256 executedHash = Hash(100);
        cache.Set(in executedHash, executed);

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < warmed.Length; i++)
            {
                ValueHash256 hash = Hash(i);
                Assert.That(cache.Get(in hash), i < 4 ? Is.SameAs(warmed[i]) : Is.Null, $"warmed code {i}");
            }

            Assert.That(view.Get(in executedHash), Is.SameAs(executed));
            Assert.That(cache.Bytes, Is.EqualTo(5 * LargeCodeCharge));
        }

        view.ClearBlock();
        Assert.That(cache.Get(in executedHash), Is.Null);
    }

    [Test]
    public void Clearing_the_block_keeps_the_process_wide_cache()
    {
        StaticCodeCache inner = new(maxCapacity: 64);
        BlockCodeCache cache = new(inner);
        ValueHash256 hash = Hash(0);
        CodeInfo code = new(new byte[LargeCodeLength]);
        cache.Set(in hash, code);

        cache.ClearBlock();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Bytes, Is.Zero);
            Assert.That(cache.Get(in hash), Is.SameAs(code));
        }

        inner.Clear();
        Assert.That(cache.Get(in hash), Is.Null);
    }

    private static CodeInfo[] Load(BlockCodeCache cache, int count)
    {
        CodeInfo[] codes = new CodeInfo[count];
        for (int i = 0; i < count; i++)
        {
            ValueHash256 hash = Hash(i);
            codes[i] = new CodeInfo(new byte[LargeCodeLength]);
            cache.Set(in hash, codes[i]);
        }

        return codes;
    }

    private static ValueHash256 Hash(int i) => ValueKeccak.Compute(BitConverter.GetBytes(i));
}
