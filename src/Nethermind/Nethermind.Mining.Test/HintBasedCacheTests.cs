// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus.Ethash;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.Mining.Test;

[Parallelizable(ParallelScope.Self)]
public class HintBasedCacheTests
{
    private class NullDataSet : IEthashDataSet
    {
        public void Dispose()
        {
        }

        public uint Size => 0;

        public uint[] CalcDataSetItem(uint i)
        {
            return [];
        }
    }

    [Test]
    public void Without_hint_return_null()
    {
        HintBasedCache hintBasedCache = new(static e => new NullDataSet(), LimboLogs.Instance);
        for (uint i = 0; i < 1000; i++)
        {
            Assert.That(hintBasedCache.Get(i), Is.Null);
        }
    }

    private readonly Guid _guidA = Guid.NewGuid();
    private readonly Guid _guidB = Guid.NewGuid();
    private readonly Guid _guidC = Guid.NewGuid();

    [Test]
    public async Task With_hint_returns_value()
    {
        HintBasedCache hintBasedCache = new(e => new NullDataSet(), LimboLogs.Instance);
        hintBasedCache.Hint(_guidA, 0, 200000);
        await WaitFor(() => hintBasedCache.CachedEpochsCount == 7);

        for (uint i = 0; i < 7; i++)
        {
            Assert.That(hintBasedCache.Get(i), Is.Not.Null);
        }
    }

    [Test]
    public void Sync_hint_and_get()
    {
        HintBasedCache hintBasedCache = new(static e => new NullDataSet(), LimboLogs.Instance);
        hintBasedCache.Hint(_guidA, 200000, 200000);
        Assert.That(hintBasedCache.Get((uint)(200000 / Ethash.EpochLength)), Is.Not.Null);
    }

    [Test]
    public async Task Many_threads()
    {
        int range = 10000000;
        HintBasedCache hintBasedCache = new(e => new NullDataSet(), LimboLogs.Instance);
        void KeepHinting(Guid guid, int start)
        {
            for (int i = start; i <= range; i++)
            {
                hintBasedCache.Hint(guid, i, i + 120000);
            }
        }

        Task a = new(() => KeepHinting(_guidA, 100000));
        Task b = new(() => KeepHinting(_guidB, 0));
        Task c = new(() => KeepHinting(_guidC, 500000));

        a.Start();
        b.Start();
        c.Start();

        await Task.WhenAll(a, b, c);

        Assert.That(hintBasedCache.CachedEpochsCount, Is.EqualTo(5));
        for (uint i = (uint)(range / Ethash.EpochLength); i < (uint)((range + 120000) / Ethash.EpochLength); i++)
        {
            Assert.That(hintBasedCache.Get(i), Is.Not.Null);
        }
    }

    [Test]
    public async Task Different_users_reuse_cached_epochs()
    {
        HintBasedCache hintBasedCache = new(e => new NullDataSet(), LimboLogs.Instance);
        hintBasedCache.Hint(_guidA, 0, 200000);
        hintBasedCache.Hint(_guidB, 0, 200000);
        await WaitFor(() => hintBasedCache.CachedEpochsCount == 7);
        for (uint i = 0; i < 7; i++)
        {
            Assert.That(hintBasedCache.Get(i), Is.Not.Null);
        }
    }

    [Test]
    public async Task Different_users_can_use_cache()
    {
        HintBasedCache hintBasedCache = new(e => new NullDataSet(), LimboLogs.Instance);
        hintBasedCache.Hint(_guidA, 0, 29999);
        hintBasedCache.Hint(_guidB, 30000, 59999);
        await WaitFor(() => hintBasedCache.CachedEpochsCount == 2);
        for (uint i = 0; i < 2; i++)
        {
            Assert.That(hintBasedCache.Get(i), Is.Not.Null);
        }
    }

    [Test]
    public async Task Different_users_can_use_disconnected_epochs()
    {
        HintBasedCache hintBasedCache = new(e => new NullDataSet(), LimboLogs.Instance);
        hintBasedCache.Hint(_guidA, 0, 29999);
        hintBasedCache.Hint(_guidB, 120000, 149999);
        await WaitFor(() => hintBasedCache.CachedEpochsCount == 2);
        Assert.That(hintBasedCache.Get(0), Is.Not.Null);
        Assert.That(hintBasedCache.Get(1), Is.Null);
        Assert.That(hintBasedCache.Get(2), Is.Null);
        Assert.That(hintBasedCache.Get(3), Is.Null);
        Assert.That(hintBasedCache.Get(4), Is.Not.Null);
    }

    [Test]
    public async Task Moving_range_evicts_cached_epochs()
    {
        HintBasedCache hintBasedCache = new(e => new NullDataSet(), LimboLogs.Instance);
        hintBasedCache.Hint(_guidA, 0, 209999);
        hintBasedCache.Hint(_guidA, 30000, 239999);
        await WaitFor(() => hintBasedCache.CachedEpochsCount == 7);

        Assert.That(hintBasedCache.Get(0), Is.Null);
        for (uint i = 1; i < 8; i++)
        {
            Assert.That(hintBasedCache.Get(i), Is.Not.Null, i.ToString());
        }
    }

    [Test]
    public async Task Can_hint_far()
    {
        HintBasedCache hintBasedCache = new(e => new NullDataSet(), LimboLogs.Instance);
        hintBasedCache.Hint(_guidA, 1000000000, 1000000000);
        await WaitFor(() => hintBasedCache.CachedEpochsCount == 1);

        Assert.That(hintBasedCache.Get(1000000000 / 30000), Is.Not.Null);
    }

    [Test]
    public void Throws_on_wide_hint()
    {
        HintBasedCache hintBasedCache = new(e => new NullDataSet(), LimboLogs.Instance);
        Assert.Throws<InvalidOperationException>(() => hintBasedCache.Hint(_guidA, 0, 1000000000));
    }

    private async Task WaitFor(Func<bool> isConditionMet, string description = "condition to be met")
    {
        const int waitInterval = 10;
        for (int i = 0; i < 10; i++)
        {
            if (isConditionMet())
            {
                return;
            }

            TestContext.Out.WriteLine($"({i}) Waiting {waitInterval} for {description}");
            await Task.Delay(waitInterval);
        }
    }
}
