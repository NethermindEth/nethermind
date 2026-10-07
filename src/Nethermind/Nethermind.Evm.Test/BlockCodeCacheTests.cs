// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
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
    public void Past_the_cap_code_of_warming_and_finished_transactions_gives_way_to_a_running_transaction()
    {
        BlockCodeCache cache = new(NoopCodeCache.Instance, maxBytes: 10 * LargeCodeCharge);
        Load(cache, count: 3);
        using (cache.BeginTransaction()) Load(cache, count: 7, first: 3);

        CodeInfo[] codes;
        using (cache.BeginTransaction()) codes = Load(cache, count: 10, first: 10);

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < 20; i++)
            {
                ValueHash256 hash = Hash(i);
                Assert.That(cache.Get(in hash), i < 10 ? Is.Null : Is.SameAs(codes[i - 10]), $"code {i}");
            }

            Assert.That(cache.Bytes, Is.EqualTo(10 * LargeCodeCharge));
        }
    }

    [Test]
    public void Past_the_cap_code_running_transactions_use_is_kept_until_they_finish()
    {
        BlockCodeCache cache = new(NoopCodeCache.Instance, maxBytes: 10 * LargeCodeCharge);
        CodeInfo[] concurrent = [];
        using ManualResetEventSlim loaded = new();
        using ManualResetEventSlim finish = new();
        Thread concurrentTransaction = new(() =>
        {
            using (cache.BeginTransaction())
            {
                concurrent = Load(cache, count: 4);
                loaded.Set();
                finish.Wait();
            }
        })
        { IsBackground = true };
        concurrentTransaction.Start();
        Assert.That(loaded.Wait(TimeSpan.FromSeconds(10)), Is.True, "concurrent transaction started");

        CodeInfo[] codes;
        CodeInfo[] afterFinish;
        using (cache.BeginTransaction())
        {
            try
            {
                codes = Load(cache, count: 10, first: 4);
                // Not looking up the concurrent transaction's code here: a hit would make it this transaction's too.
                using (Assert.EnterMultipleScope())
                {
                    for (int i = 4; i < 14; i++)
                    {
                        ValueHash256 hash = Hash(i);
                        Assert.That(cache.Get(in hash), i < 10 ? Is.SameAs(codes[i - 4]) : Is.Null, $"code {i}");
                    }

                    Assert.That(cache.Bytes, Is.EqualTo(10 * LargeCodeCharge), "the concurrent transaction's code kept");
                }
            }
            finally
            {
                finish.Set();
            }

            Assert.That(concurrentTransaction.Join(TimeSpan.FromSeconds(10)), Is.True, "concurrent transaction finished");
            afterFinish = Load(cache, count: 4, first: 14);
        }

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < 18; i++)
            {
                ValueHash256 hash = Hash(i);
                Assert.That(cache.Get(in hash), i is >= 4 and < 10 ? Is.SameAs(codes[i - 4]) : i >= 14 ? Is.SameAs(afterFinish[i - 14]) : Is.Null, $"code {i} after the concurrent transaction finished");
            }

            Assert.That(cache.Bytes, Is.EqualTo(10 * LargeCodeCharge));
        }
    }

    [Test]
    public void Past_the_cap_code_a_running_transaction_hit_is_kept()
    {
        BlockCodeCache cache = new(NoopCodeCache.Instance, maxBytes: 10 * LargeCodeCharge);
        CodeInfo[] warmed = Load(cache, count: 4);

        CodeInfo[] codes;
        using (cache.BeginTransaction())
        {
            for (int i = 0; i < warmed.Length; i++)
            {
                ValueHash256 hash = Hash(i);
                cache.Get(in hash);
            }

            codes = Load(cache, count: 10, first: 4);
        }

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < 14; i++)
            {
                ValueHash256 hash = Hash(i);
                Assert.That(cache.Get(in hash), i < 4 ? Is.SameAs(warmed[i]) : i < 10 ? Is.SameAs(codes[i - 4]) : Is.Null, $"code {i}");
            }
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

    private static CodeInfo[] Load(BlockCodeCache cache, int count, int first = 0)
    {
        CodeInfo[] codes = new CodeInfo[count];
        for (int i = 0; i < count; i++)
        {
            ValueHash256 hash = Hash(first + i);
            codes[i] = new CodeInfo(new byte[LargeCodeLength]);
            cache.Set(in hash, codes[i]);
        }

        return codes;
    }

    private static ValueHash256 Hash(int i) => ValueKeccak.Compute(BitConverter.GetBytes(i));
}
