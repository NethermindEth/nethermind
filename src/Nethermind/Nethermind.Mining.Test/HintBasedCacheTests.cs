// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Ethash;
using Nethermind.Core.Events;
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

        public uint[] CalcDataSetItem(uint i) => [];
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
        await Wait.ForCondition(() => hintBasedCache.CachedEpochsCount == 7, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10));

        using (Assert.EnterMultipleScope())
        {
            for (uint i = 0; i < 7; i++)
            {
                Assert.That(hintBasedCache.Get(i), Is.Not.Null, i.ToString());
            }

            Assert.That(hintBasedCache.Get(7), Is.Null);
        }
    }

    [Test]
    public void Get_returns_the_cached_instance()
    {
        HintBasedCache hintBasedCache = new(static e => new NullDataSet(), LimboLogs.Instance);
        hintBasedCache.Hint(_guidA, 200000, 200000);
        IEthashDataSet firstGet = hintBasedCache.Get(Ethash.GetEpoch(200000UL));
        Assert.That(hintBasedCache.Get(Ethash.GetEpoch(200000UL)), Is.Not.Null.And.SameAs(firstGet));
    }

    [Test]
    public void Sync_hint_and_get()
    {
        HintBasedCache hintBasedCache = new(static e => new NullDataSet(), LimboLogs.Instance);
        hintBasedCache.Hint(_guidA, 200000, 200000);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hintBasedCache.CachedEpochsCount, Is.EqualTo(1));
            Assert.That(hintBasedCache.Get(Ethash.GetEpoch(200000UL)), Is.Not.Null);
            Assert.That(hintBasedCache.Get(Ethash.GetEpoch(200000UL) - 1), Is.Null);
            Assert.That(hintBasedCache.Get(Ethash.GetEpoch(200000UL) + 1), Is.Null);
        }
    }

    [Test]
    public async Task Many_threads()
    {
        ulong range = 10000000UL;
        HintBasedCache hintBasedCache = new(e => new NullDataSet(), LimboLogs.Instance);
        void KeepHinting(Guid guid, ulong start)
        {
            for (ulong i = start; i <= range; i++)
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

        uint firstEpoch = (uint)(range / Ethash.EpochLength);
        uint lastEpoch = (uint)((range + 120000) / Ethash.EpochLength);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hintBasedCache.CachedEpochsCount, Is.EqualTo(5));
            for (uint i = firstEpoch; i <= lastEpoch; i++)
            {
                Assert.That(hintBasedCache.Get(i), Is.Not.Null, i.ToString());
            }

            Assert.That(hintBasedCache.Get(firstEpoch - 1), Is.Null);
            Assert.That(hintBasedCache.Get(lastEpoch + 1), Is.Null);
        }
    }

    /// <remarks>
    /// A tripwire for lookups racing with another user's hints: an unsynchronized lookup only misses when a reader walks
    /// the table while a hint unlinks or adds an entry, so this test can pass on broken code.
    /// </remarks>
    [Test, MaxTime(60_000)]
    public async Task Pinned_epoch_stays_readable_while_another_user_moves_its_window()
    {
        HintBasedCache hintBasedCache = new(static _ => new NullDataSet(), LimboLogs.Instance);
        hintBasedCache.Hint(_guidA, 0, 0);
        bool hinting = true;
        Task<int>[] readers = new Task<int>[4];
        using CountdownEvent readersStarted = new(readers.Length);
        for (int i = 0; i < readers.Length; i++)
        {
            readers[i] = Task.Factory.StartNew(() =>
            {
                readersStarted.Signal();
                int misses = 0;
                while (Volatile.Read(ref hinting))
                {
                    if (hintBasedCache.Get(0) is null) misses++;
                }

                return misses;
            }, TaskCreationOptions.LongRunning);
        }

        try
        {
            Assert.That(readersStarted.Wait(TimeSpan.FromSeconds(10)), Is.True, "readers started");
            // Alternating between two disjoint windows removes 11 epochs and adds 11 on every hint.
            for (int i = 0; i < 2_000; i++)
            {
                ulong startEpoch = i % 2 == 0 ? 1UL : 12UL;
                hintBasedCache.Hint(_guidB, startEpoch * Ethash.EpochLength, (startEpoch + 10) * Ethash.EpochLength);
            }
        }
        finally
        {
            Volatile.Write(ref hinting, false);
        }

        Assert.That(await Task.WhenAll(readers), Is.All.EqualTo(0), "lookups of the pinned epoch that missed");
    }

    [Test]
    public async Task Different_users_reuse_cached_epochs()
    {
        HintBasedCache hintBasedCache = new(e => new NullDataSet(), LimboLogs.Instance);
        hintBasedCache.Hint(_guidA, 0, 200000);
        hintBasedCache.Hint(_guidB, 0, 200000);
        await Wait.ForCondition(() => hintBasedCache.CachedEpochsCount == 7, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10));
        using (Assert.EnterMultipleScope())
        {
            for (uint i = 0; i < 7; i++)
            {
                Assert.That(hintBasedCache.Get(i), Is.Not.Null, i.ToString());
            }

            Assert.That(hintBasedCache.Get(7), Is.Null);
        }
    }

    [Test]
    public async Task Different_users_can_use_cache()
    {
        HintBasedCache hintBasedCache = new(e => new NullDataSet(), LimboLogs.Instance);
        hintBasedCache.Hint(_guidA, 0, 29999);
        hintBasedCache.Hint(_guidB, 30000, 59999);
        await Wait.ForCondition(() => hintBasedCache.CachedEpochsCount == 2, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10));
        using (Assert.EnterMultipleScope())
        {
            for (uint i = 0; i < 2; i++)
            {
                Assert.That(hintBasedCache.Get(i), Is.Not.Null, i.ToString());
            }

            Assert.That(hintBasedCache.Get(2), Is.Null);
        }
    }

    [Test]
    public async Task Different_users_can_use_disconnected_epochs()
    {
        HintBasedCache hintBasedCache = new(e => new NullDataSet(), LimboLogs.Instance);
        hintBasedCache.Hint(_guidA, 0, 29999);
        hintBasedCache.Hint(_guidB, 120000, 149999);
        await Wait.ForCondition(() => hintBasedCache.CachedEpochsCount == 2, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(hintBasedCache.Get(0), Is.Not.Null);
            Assert.That(hintBasedCache.Get(1), Is.Null);
            Assert.That(hintBasedCache.Get(2), Is.Null);
            Assert.That(hintBasedCache.Get(3), Is.Null);
            Assert.That(hintBasedCache.Get(4), Is.Not.Null);
        }
    }

    [Test]
    public async Task Moving_range_evicts_cached_epochs()
    {
        HintBasedCache hintBasedCache = new(e => new NullDataSet(), LimboLogs.Instance);
        hintBasedCache.Hint(_guidA, 0, 209999);
        hintBasedCache.Hint(_guidA, 30000, 239999);
        await Wait.ForCondition(() => hintBasedCache.CachedEpochsCount == 7, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hintBasedCache.Get(0), Is.Null);
            for (uint i = 1; i < 8; i++)
            {
                Assert.That(hintBasedCache.Get(i), Is.Not.Null, i.ToString());
            }
        }
    }

    [Test]
    public async Task Can_hint_far()
    {
        HintBasedCache hintBasedCache = new(e => new NullDataSet(), LimboLogs.Instance);
        hintBasedCache.Hint(_guidA, 1000000000, 1000000000);
        await Wait.ForCondition(() => hintBasedCache.CachedEpochsCount == 1, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hintBasedCache.Get(Ethash.GetEpoch(1000000000UL)), Is.Not.Null);
            Assert.That(hintBasedCache.Get(Ethash.GetEpoch(1000000000UL) - 1), Is.Null);
            Assert.That(hintBasedCache.Get(Ethash.GetEpoch(1000000000UL) + 1), Is.Null);
        }
    }

    [Test]
    public void Throws_on_wide_hint()
    {
        HintBasedCache hintBasedCache = new(e => new NullDataSet(), LimboLogs.Instance);
        Assert.Throws<InvalidOperationException>(() => hintBasedCache.Hint(_guidA, 0, 1000000000));
    }

    [Test]
    public void Throws_on_hint_above_the_maximum_epoch()
    {
        HintBasedCache hintBasedCache = new(static e => new NullDataSet(), LimboLogs.Instance);
        ulong blockNumber = (ulong)uint.MaxValue * Ethash.EpochLength;

        Assert.Throws<InvalidOperationException>(() => hintBasedCache.Hint(_guidA, blockNumber, blockNumber));
    }

    [Test]
    public void Throws_on_hint_whose_epoch_only_fits_uint_when_truncated()
    {
        HintBasedCache hintBasedCache = new(static e => new NullDataSet(), LimboLogs.Instance);
        // Truncating this epoch to uint would wrap it to 5 and accept it as an ordinary early epoch.
        ulong epoch = (1UL << 32) + 5;
        ulong blockNumber = epoch * Ethash.EpochLength;

        Assert.Throws<InvalidOperationException>(() => hintBasedCache.Hint(_guidA, blockNumber, blockNumber));
    }

    [Test]
    public void Builds_a_single_epoch_at_the_maximum_supported_epoch()
    {
        HintBasedCache hintBasedCache = new(static e => new NullDataSet(), LimboLogs.Instance);
        ulong blockNumber = Ethash.MaxEpoch * Ethash.EpochLength;

        hintBasedCache.Hint(_guidA, blockNumber, blockNumber);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(hintBasedCache.CachedEpochsCount, Is.EqualTo(1));
            Assert.That(hintBasedCache.Get(Ethash.MaxEpoch), Is.Not.Null);
        }
    }
}
