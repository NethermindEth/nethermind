// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

[TestFixture]
public class BalReadStoragePlanTests
{
    [Test]
    public void Coverage_reduces_workers_and_checks_partial_words(
        [Values(0, 1, 7, 8, 9, 63, 64, 65, 511, 512, 513)] int count, [Values] bool omitLast, [Values] bool wideKeys,
        [Values(1, 2, 8)] int workerCount)
    {
        UInt256[] slots = new UInt256[count];
        for (int i = 0; i < count; i++)
            slots[i] = wideKeys ? new UInt256((ulong)(count - i), (ulong)i, (ulong)i / 2, (ulong)i / 4) : (UInt256)i;
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(
            Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(slots).TestObject).TestObject;
        using BalReadStoragePlan plan = new(bal);
        BalReadCoverage[] workers = new BalReadCoverage[workerCount];
        for (int i = 0; i < workers.Length; i++) workers[i] = plan.CreateCoverage();
        int marked = omitLast ? Math.Max(0, count - 1) : count;
        for (int i = 0; i < marked; i++)
        {
            StorageCell cell = new(TestItem.AddressA, slots[i]);
            workers[i % workerCount].TryMark(cell);
            workers[i % workerCount].TryMark(cell);
        }

        Assert.That(plan.TryFindUncovered(out _), Is.EqualTo(omitLast && count > 0));
    }

    [Test]
    public void Coverage_resolves_collisions_and_rejects_missing_keys([Values] bool collideAccounts)
    {
        List<StorageCell> reads = [];
        ReadOnlyAccountChanges[] accounts = new ReadOnlyAccountChanges[collideAccounts ? 16 : 1];
        for (int account = 0; account < accounts.Length; account++)
        {
            Address address = collideAccounts ? CollidingAddress(account + 1) : TestItem.AddressA;
            UInt256[] slots = new UInt256[collideAccounts ? account % 3 + 1 : 16];
            for (int i = 0; i < slots.Length; i++)
            {
                ulong limb = (ulong)i + 1;
                slots[i] = collideAccounts ? (UInt256)limb : new UInt256(limb, limb, limb, limb);
                reads.Add(new StorageCell(address, slots[i]));
            }
            accounts[account] = new(address, [], slots, [], [], []);
        }
        using BalReadStoragePlan plan = new(new ReadOnlyBlockAccessList(accounts, accounts.Length + reads.Count));
        // Inspect the bounded cache so these lookups must exercise the binary-search fallback.
        int[] index = (int[])typeof(BalReadStoragePlan)
            .GetField(collideAccounts ? "_addressIndex" : "_slotIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plan)!;
        Assert.That(index.AsSpan(0, 4), Is.SequenceEqualTo(new[] { 1, 2, 3, 4 }));
        Assert.That(index.AsSpan(0, 32).ToArray(), Does.Not.Contain(5));
        BalReadCoverage coverage = plan.CreateCoverage();
        for (int i = 0; i < reads.Count; i++)
        {
            StorageCell cell = reads[i];
#pragma warning disable NUnit2045 // Each state transition must succeed before the following operation or state check.
            Assert.That(plan.TryGetOrdinal(new StorageCell(new Address(cell.Address.Bytes), cell.Index), out int ordinal), Is.True);
#pragma warning restore NUnit2045
            Assert.That(ordinal, Is.EqualTo(i));
            Assert.That(coverage.TryMark(cell), Is.True);
        }
        StorageCell missing = collideAccounts
            ? new StorageCell(CollidingAddress(17), UInt256.One)
            : new StorageCell(TestItem.AddressA, new UInt256(17, 17, 17, 17));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.TryGetOrdinal(missing, out _), Is.False);
            Assert.That(plan.TryGetOrdinal(new StorageCell(reads[0].Address, UInt256.MaxValue), out _), Is.False);
            if (!collideAccounts) Assert.That(plan.TryGetOrdinal(new StorageCell(TestItem.AddressA, UInt256.One), out _), Is.False);
            Assert.That(plan.TryFindUncovered(out _), Is.False);
        }

        static Address CollidingAddress(int index)
        {
            byte[] bytes = new byte[Address.Size];
            bytes[0] = bytes[8] = (byte)index;
            return new Address(bytes);
        }
    }

    [Test]
    public void Coverage_distinguishes_accounts_and_reuses_cached_hits_and_misses()
    {
        StorageCell first = new(TestItem.AddressA, 1);
        StorageCell last = new(TestItem.AddressA, 3);
        StorageCell other = new(TestItem.AddressB, 1);
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(
            Build.An.AccountChanges.WithAddress(first.Address).WithStorageReads(first.Index, last.Index).TestObject,
            Build.An.AccountChanges.WithAddress(other.Address).WithStorageReads(other.Index).TestObject).TestObject;
        using BalReadStoragePlan plan = new(bal);
        BalReadCoverage worker = plan.CreateCoverage();
        StorageCell[] misses = [new(first.Address, 0), new(first.Address, 2), new(first.Address, 4), new(TestItem.AddressC, 1)];
        foreach (StorageCell miss in misses)
        {
            Assert.That(worker.TryMark(miss), Is.False);
            Assert.That(worker.TryMark(miss), Is.False);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(plan.TryFindUncovered(out Address? uncovered), Is.True);
            Assert.That(uncovered, Is.EqualTo(bal.AccountChanges.AsSpan()[0].Address));
        }
#pragma warning disable NUnit2045 // Each TryMark changes the coverage the following assertion checks.
        Assert.That(worker.TryMark(first), Is.True);
        Assert.That(worker.TryMark(new StorageCell(new Address(first.Address.Bytes), first.Index)), Is.True);
        Assert.That(worker.TryMark(other), Is.True);
        Assert.That(plan.TryFindUncovered(out _), Is.True);
        Assert.That(worker.TryMark(last), Is.True);
        Assert.That(plan.TryFindUncovered(out _), Is.False);
#pragma warning restore NUnit2045
        plan.Dispose();
        Assert.Throws<ObjectDisposedException>(() => worker.TryMark(last));
        Assert.Throws<ObjectDisposedException>(() => plan.TryGetOrdinal(last, out _));
    }

    [Test]
    public void Coverage_clears_dirty_rentals_and_ignores_excess_capacity([Values(65, 513)] int count)
    {
        UInt256[] slots = new UInt256[count];
        for (int i = 0; i < count; i++) slots[i] = (UInt256)i;
        using BalReadStoragePlan plan = new(Build.A.BlockAccessList.WithAccountChanges(
            Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithStorageReads(slots).TestObject).TestObject);
        DirtyCoveragePool pool = new();
        BalReadCoverage worker = new(plan, pool);
        try
        {
            Assert.That(worker.FirstUncovered(), Is.Zero);
            for (int i = 0; i < count - 1; i++) worker.TryMark(new StorageCell(TestItem.AddressA, slots[i]));
            Assert.That(worker.FirstUncovered(), Is.EqualTo(count - 1));
            worker.TryMark(new StorageCell(TestItem.AddressA, slots[^1]));
            Assert.That(worker.FirstUncovered(), Is.EqualTo(-1));
        }
        finally
        {
            worker.Release();
        }
        worker.Release();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(pool.Returns, Is.EqualTo(1));
            Assert.That(worker.Plan, Is.Null);
        }
        Assert.Throws<ObjectDisposedException>(() => worker.TryMark(new StorageCell(TestItem.AddressA, slots[^1])));
        BalReadCoverage reused = new(plan, pool);
        try
        {
            Assert.That(reused.FirstUncovered(), Is.Zero);
            reused.TryMark(new StorageCell(TestItem.AddressA, slots[^1]));
            Assert.That(reused.FirstUncovered(), Is.Zero);
        }
        finally
        {
            reused.Release();
        }
        Assert.That(pool.Returns, Is.EqualTo(2));
    }

    private sealed class DirtyCoveragePool : ArrayPool<ulong>
    {
        public ulong[] Buffer { get; private set; } = [];
        public int Returns { get; private set; }

        public override ulong[] Rent(int minimumLength)
        {
            if (Buffer.Length == 0)
            {
                Buffer = new ulong[minimumLength + 7];
                Array.Fill(Buffer, ulong.MaxValue);
            }
            return Buffer;
        }

        public override void Return(ulong[] array, bool clearArray = false)
        {
            Assert.That(array, Is.SameAs(Buffer));
            Returns++;
            if (clearArray) Array.Clear(array);
        }
    }
}
