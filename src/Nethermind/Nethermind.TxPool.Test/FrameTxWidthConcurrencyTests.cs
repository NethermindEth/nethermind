// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

/// <summary>
/// The lock-free width ledger under contention: admission must never spend more width than was earned, a
/// racing credit must never be dropped, spent width is never returned, and the sender gauge must return to
/// zero once every balance drains. These are the invariants that make MATCHA width a DoS bound rather than
/// a suggestion.
/// </summary>
[TestFixture]
[NonParallelizable]
public class FrameTxWidthConcurrencyTests
{
    private static readonly Address Sender = new(new byte[20] { 0xa1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
    private const ulong Cost = 21_000;

    [Test]
    public void ConcurrentSpends_SpendExactlyTheEarnedBudget_NeverOversell()
    {
        const int admits = 500;
        const int attempts = 4_000;
        SenderWidthCache cache = new();
        cache.Earn(Sender, (UInt256)(Cost * admits));

        int succeeded = 0;
        Parallel.For(0, attempts, new ParallelOptions { MaxDegreeOfParallelism = 64 }, _ =>
        {
            if (cache.TrySpend(Sender, Cost)) Interlocked.Increment(ref succeeded);
        });

        using (Assert.EnterMultipleScope())
        {
            Assert.That(succeeded, Is.EqualTo(admits), "exactly the earned budget is admitted, never more");
            Assert.That(cache.GetWidth(Sender), Is.EqualTo(UInt256.Zero), "no width is left unspent or oversold below zero");
        }
    }

    [Test]
    public void ConcurrentEarnAndSpend_ConservesEveryEarnedUnit()
    {
        const int rounds = 20_000;
        SenderWidthCache cache = new();
        long spent = 0;

        Parallel.Invoke(
            () => { for (int i = 0; i < rounds; i++) cache.Earn(Sender, Cost); },
            () => { for (int i = 0; i < rounds; i++) if (cache.TrySpend(Sender, Cost)) Interlocked.Add(ref spent, (long)Cost); });

        while (cache.TrySpend(Sender, Cost)) { spent += (long)Cost; }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.GetWidth(Sender), Is.EqualTo(UInt256.Zero), "the ledger drains to zero with nothing stranded");
            Assert.That((ulong)spent, Is.EqualTo(Cost * rounds), "every earned unit is spendable exactly once, none minted or lost");
        }
    }

    [Test]
    public void SpentWidth_IsNeverReturned()
    {
        SenderWidthCache cache = new();
        cache.Earn(Sender, Cost);

        Assert.That(cache.TrySpend(Sender, Cost), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.GetWidth(Sender), Is.EqualTo(UInt256.Zero), "the spend consumed the whole balance");
            Assert.That(cache.TrySpend(Sender, Cost), Is.False, "nothing returns spent width, so a second spend cannot succeed without re-earning");
        }
    }

    [Test]
    public void SpendToZero_RacingACredit_NeverLosesTheCredit()
    {
        const int iterations = 50_000;
        SenderWidthCache cache = new();
        long credited = 0;
        long spent = 0;

        Parallel.Invoke(
            () => { for (int i = 0; i < iterations; i++) { cache.Earn(Sender, Cost); Interlocked.Add(ref credited, (long)Cost); } },
            () => { for (int i = 0; i < iterations; i++) if (cache.TrySpend(Sender, Cost)) Interlocked.Add(ref spent, (long)Cost); });

        while (cache.TrySpend(Sender, Cost)) spent += (long)Cost;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spent, Is.EqualTo(credited), "spend total equals credit total: no credit was dropped at the zero boundary");
            Assert.That(cache.GetWidth(Sender), Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public void SenderGauge_ReturnsToBaseline_AfterConcurrentChurnToZero()
    {
        const int senders = 256;
        long before = Volatile.Read(ref Metrics.FrameTxSendersWithWidth);
        SenderWidthCache cache = new();
        Address[] all = BuildSenders(senders);

        Parallel.ForEach(all, s => cache.Earn(s, Cost));
        long peak = Volatile.Read(ref Metrics.FrameTxSendersWithWidth) - before;
        Parallel.ForEach(all, s => Assert.That(cache.TrySpend(s, Cost), Is.True));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peak, Is.EqualTo(senders), "each first credit raises the gauge exactly once");
            Assert.That(Volatile.Read(ref Metrics.FrameTxSendersWithWidth) - before, Is.EqualTo(0),
                "draining every sender to zero returns the gauge to baseline, so an idle pool reads no width");
        }
    }

    [Test]
    public void Clear_ConcurrentWithSpends_LeavesNoGaugeLeak()
    {
        const int senders = 128;
        long before = Volatile.Read(ref Metrics.FrameTxSendersWithWidth);
        SenderWidthCache cache = new();
        Address[] all = BuildSenders(senders);
        foreach (Address s in all) cache.Earn(s, Cost);

        Parallel.Invoke(
            () => cache.Clear(),
            () => { foreach (Address s in all) cache.TrySpend(s, Cost); });

        cache.Clear();
        Assert.That(Volatile.Read(ref Metrics.FrameTxSendersWithWidth) - before, Is.EqualTo(0),
            "teardown racing spends still retires every gauge increment exactly once");
    }

    private static Address[] BuildSenders(int count)
    {
        Address[] all = new Address[count];
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = new byte[20];
            bytes[0] = 0xb0;
            bytes[18] = (byte)(i >> 8);
            bytes[19] = (byte)i;
            all[i] = new Address(bytes);
        }

        return all;
    }
}
