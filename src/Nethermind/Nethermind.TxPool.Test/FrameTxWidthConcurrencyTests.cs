// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Linq;
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
    private static readonly Address Other = new(new byte[20] { 0xa2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
    private static readonly Address Newcomer = new(new byte[20] { 0xa3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
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
    public void ConcurrentEarnAndSpend_ConservesEveryEarnedUnit([Values(20_000, 50_000)] int rounds)
    {
        SenderWidthCache cache = new();
        long spent = 0;

        Parallel.Invoke(
            () => { for (int i = 0; i < rounds; i++) cache.Earn(Sender, Cost); },
            () => { for (int i = 0; i < rounds; i++) if (cache.TrySpend(Sender, Cost)) Interlocked.Add(ref spent, (long)Cost); });

        while (cache.TrySpend(Sender, Cost)) { spent += (long)Cost; }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.GetWidth(Sender), Is.EqualTo(UInt256.Zero), "the ledger drains to zero with nothing stranded");
            Assert.That((ulong)spent, Is.EqualTo(Cost * (ulong)rounds), "every earned unit is spendable exactly once, none minted or lost");
        }
    }

    [Test]
    public void FullLedger_EvictsASenderToAdmitANewEarner()
    {
        SenderWidthCache cache = FullLedgerOfSenderAndOther();

        cache.Earn(Newcomer, 2 * Cost);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Count, Is.EqualTo(2));
            Assert.That(cache.GetWidth(Newcomer), Is.EqualTo((UInt256)(2 * Cost)));
        }
    }

    [Test]
    public void FullLedger_EvictsTheSmallestBalance()
    {
        SenderWidthCache cache = new(maxSenders: 2);
        cache.Earn(Sender, 3 * Cost);
        cache.Earn(Other, Cost);

        cache.Earn(Newcomer, 2 * Cost);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Count, Is.EqualTo(2));
            Assert.That(cache.GetWidth(Sender), Is.EqualTo((UInt256)(3 * Cost)));
            Assert.That(cache.GetWidth(Other), Is.EqualTo(UInt256.Zero));
            Assert.That(cache.GetWidth(Newcomer), Is.EqualTo((UInt256)(2 * Cost)));
        }
    }

    [Test]
    public void FullLedger_HolderWithReservedWidth_IsNotEvictedAndGetsItsRefund([Values(Cost - 1, Cost)] ulong reserved)
    {
        SenderWidthCache cache = FullLedgerOfSenderAndOther();
        cache.TryReserve(Sender, reserved);
        cache.Earn(Newcomer, Cost);

        cache.Release(Sender, reserved);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Count, Is.EqualTo(2));
            Assert.That(cache.GetWidth(Sender), Is.EqualTo((UInt256)Cost));
            Assert.That(cache.GetWidth(Other), Is.EqualTo((UInt256)Cost));
            Assert.That(cache.GetWidth(Newcomer), Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public void FullLedger_HolderWhoseReservationSettled_GivesWayToARicherNewcomer([Values(Cost - 1, Cost)] ulong reserved)
    {
        SenderWidthCache cache = FullLedgerOfSenderAndOther();
        cache.TryReserve(Sender, reserved);

        cache.Release(Sender, UInt256.Zero);
        cache.Earn(Newcomer, Cost);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.Count, Is.EqualTo(2));
            Assert.That(cache.GetWidth(Sender), Is.EqualTo(UInt256.Zero));
            Assert.That(cache.GetWidth(Other), Is.EqualTo((UInt256)Cost));
            Assert.That(cache.GetWidth(Newcomer), Is.EqualTo((UInt256)Cost));
        }
    }

    [Test]
    public void FullLedger_RefusesANewcomerNoRicherThanTheSmallestBalance([Values(1ul, Cost)] ulong newcomerGas)
    {
        SenderWidthCache cache = FullLedgerOfSenderAndOther();

        cache.Earn(Newcomer, newcomerGas);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(cache.GetWidth(Newcomer), Is.EqualTo(UInt256.Zero));
            Assert.That(cache.GetWidth(Sender), Is.EqualTo((UInt256)Cost));
            Assert.That(cache.GetWidth(Other), Is.EqualTo((UInt256)Cost));
        }
    }

    [Test]
    public void FullLedger_EvictionReachesEverySender()
    {
        const int capacity = 256;
        SenderWidthCache cache = new(maxSenders: capacity);
        Address[] incumbents = new Address[capacity];
        for (int i = 0; i < capacity; i++)
        {
            incumbents[i] = SenderAt(i);
            cache.Earn(incumbents[i], Cost);
        }

        for (int i = 0; i < 16 * capacity && incumbents.Any(a => !cache.GetWidth(a).IsZero); i++)
        {
            cache.Earn(SenderAt(capacity + i), 2 * Cost);
        }

        Assert.That(incumbents.Where(a => !cache.GetWidth(a).IsZero), Is.Empty);
    }

    private static SenderWidthCache FullLedgerOfSenderAndOther()
    {
        SenderWidthCache cache = new(maxSenders: 2);
        cache.Earn(Sender, Cost);
        cache.Earn(Other, Cost);
        return cache;
    }

    private static Address SenderAt(int index)
    {
        byte[] bytes = new byte[20];
        BitConverter.GetBytes(index + 1).CopyTo(bytes, 0);
        bytes[19] = 0xb0;
        return new Address(bytes);
    }

    [Test]
    public void SenderGauge_ReturnsToBaseline_AfterConcurrentChurnToZero([Values] bool holdsPaymasters)
    {
        const int senders = 256;
        ref long gauge = ref holdsPaymasters ? ref Metrics.FrameTxPaymastersWithWidth : ref Metrics.FrameTxSendersWithWidth;
        ref long otherGauge = ref holdsPaymasters ? ref Metrics.FrameTxSendersWithWidth : ref Metrics.FrameTxPaymastersWithWidth;
        long before = Volatile.Read(ref gauge);
        long otherBefore = Volatile.Read(ref otherGauge);
        SenderWidthCache cache = new(holdsPaymasters: holdsPaymasters);
        Address[] all = BuildSenders(senders);

        Parallel.ForEach(all, s => cache.Earn(s, Cost));
        long peak = Volatile.Read(ref gauge) - before;
        long otherPeak = Volatile.Read(ref otherGauge) - otherBefore;
        Parallel.ForEach(all, s => Assert.That(cache.TrySpend(s, Cost), Is.True));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peak, Is.EqualTo(senders), "each first credit raises the gauge exactly once");
            Assert.That(otherPeak, Is.Zero, "senders and paymasters are counted on separate gauges");
            Assert.That(Volatile.Read(ref gauge) - before, Is.EqualTo(0),
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
