// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Reflection;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

/// <summary>
/// Journal/snapshot tests for the per-tx <see cref="BlockAccessListAtIndex"/> slice. The slice's
/// mutators (<see cref="BlockAccessListAtIndex.AddBalanceChange"/>, <c>AddNonceChange</c>,
/// <c>AddCodeChange</c>, <c>AddStorageChange</c>, <c>DeleteAccount</c>) push undo records onto an
/// internal <c>_changes</c> log so <see cref="BlockAccessListAtIndex.Restore"/> can revert to a
/// prior snapshot — exercised by EVM revert paths through <see cref="Nethermind.State.TracedAccessWorldState"/>.
/// </summary>
[TestFixture]
public class BlockAccessListJournalTests
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
        ulong chargeableReads = 0;
        foreach (BalReadCoverage worker in workers) chargeableReads += worker.ChargeableReadCount;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chargeableReads, Is.EqualTo((ulong)marked));
            Assert.That(plan.TryFindUncovered(out _), Is.EqualTo(omitLast && count > 0));
        }
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
        Assert.That(index.AsSpan(0, 4).ToArray(), Is.EqualTo(new[] { 1, 2, 3, 4 }));
        Assert.That(index.AsSpan(0, 32).ToArray(), Does.Not.Contain(5));
        BalReadCoverage coverage = plan.CreateCoverage();
        for (int i = 0; i < reads.Count; i++)
        {
            StorageCell cell = reads[i];
            Assert.That(plan.TryGetOrdinal(new StorageCell(new Address(cell.Address.Bytes), cell.Index), out int ordinal), Is.True);
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
            Assert.That(coverage.ChargeableReadCount, Is.EqualTo(reads.Count));
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
        for (int slice = 0; slice < 2; slice++)
        {
            worker.StartSlice();
            foreach (StorageCell miss in misses)
            {
                Assert.That(worker.TryMark(miss), Is.False);
                Assert.That(worker.TryMark(miss), Is.False);
            }
            Assert.That(worker.ChargeableReadCount, Is.Zero);
            Assert.That(worker.TryMark(first), Is.True);
            Assert.That(worker.TryMark(new StorageCell(new Address(first.Address.Bytes), first.Index)), Is.True);
            Assert.That(worker.ChargeableReadCount, Is.EqualTo(1));
            Assert.That(worker.TryMark(other), Is.True);
            Assert.That(worker.ChargeableReadCount, Is.EqualTo(2));
            Assert.That(worker.TryMark(last), Is.True);
            Assert.That(worker.ChargeableReadCount, Is.EqualTo(3));
        }
        Assert.That(plan.TryFindUncovered(out _), Is.False);
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
            StorageCell last = new(TestItem.AddressA, slots[^1]);
            worker.TryMark(last);
            Assert.That(worker.FirstUncovered(), Is.EqualTo(-1));
            worker.StartSlice();
            worker.TryMark(last);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(worker.ChargeableReadCount, Is.EqualTo(1));
                Assert.That(worker.FirstUncovered(), Is.EqualTo(-1));
            }
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
            Assert.That(reused.ChargeableReadCount, Is.Zero);
            reused.TryMark(new StorageCell(TestItem.AddressA, slots[^1]));
            Assert.That(reused.ChargeableReadCount, Is.EqualTo(1));
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

    private static readonly Address[] SystemAddresses =
    [
        Eip7002Constants.WithdrawalRequestPredeployAddress,
        Eip7251Constants.ConsolidationRequestPredeployAddress,
        Eip8282Constants.BuilderDepositRequestPredeployAddress,
        Eip8282Constants.BuilderExitRequestPredeployAddress
    ];

    [Test]
    public void Coverage_reuses_slice_storage_and_excludes_system_reads(
        [ValueSource(nameof(SystemAddresses))] Address systemAddress, [Values(1, 65)] int systemReads)
    {
        StorageCell cell = new(TestItem.AddressA, 1);
        StorageCell system = new(systemAddress, 2);
        UInt256[] systemSlots = new UInt256[systemReads];
        for (int i = 0; i < systemSlots.Length; i++) systemSlots[i] = (UInt256)(i + 2);
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList.WithAccountChanges(
            Build.An.AccountChanges.WithAddress(cell.Address).WithStorageReads(cell.Index).TestObject,
            Build.An.AccountChanges.WithAddress(system.Address).WithStorageReads(systemSlots).TestObject).TestObject;
        using BalReadStoragePlan plan = new(bal);
        BalReadCoverage worker = plan.CreateCoverage();
        worker.TryMark(cell);
        foreach (UInt256 slot in systemSlots) worker.TryMark(new StorageCell(systemAddress, slot));
        Assert.That(worker.ChargeableReadCount, Is.EqualTo(1));
        for (int i = 0; i < 10_000; i++)
        {
            worker.StartSlice();
            worker.TryMark(cell);
            worker.TryMark(cell);
            worker.TryMark(system);
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(worker.ChargeableReadCount, Is.EqualTo(1));
            Assert.That(plan.TryFindUncovered(out _), Is.False, "earlier slices still cover system reads");
        }
        plan.Dispose();
        Assert.That(worker.Plan, Is.Null);
        Assert.Throws<ObjectDisposedException>(() => worker.TryMark(cell));
    }

    [Test]
    public void AddCodeChange_with_equal_before_after_does_not_create_account_changes()
    {
        BlockAccessListAtIndex slice = new() { Index = 0 };
        byte[] emptyCode = [];

        slice.AddCodeChange(TestItem.AddressA, emptyCode, emptyCode);

        Assert.That(slice.GetAccountChanges(TestItem.AddressA), Is.Null);
    }

    [Test]
    public void Restore_reinstates_previous_values_for_interleaved_change_types()
    {
        BlockAccessListAtIndex slice = new() { Index = 1 };

        byte[] emptyCode = [];
        byte[] codeBeforeSnapshot = [0x60];
        byte[] codeAfterSnapshot = [0x61];
        UInt256 slot = 7;

        slice.AddBalanceChange(TestItem.AddressA, before: 0, after: 10);
        slice.AddNonceChange(TestItem.AddressA, 1);
        slice.AddCodeChange(TestItem.AddressA, emptyCode, codeBeforeSnapshot);
        slice.AddStorageChange(TestItem.AddressA, slot, before: 0, after: 11);

        int snapshot = slice.TakeSnapshot();

        slice.AddBalanceChange(TestItem.AddressA, before: 10, after: 20);
        slice.AddNonceChange(TestItem.AddressA, 2);
        slice.AddCodeChange(TestItem.AddressA, codeBeforeSnapshot, codeAfterSnapshot);
        slice.AddStorageChange(TestItem.AddressA, slot, before: 11, after: 22);

        slice.Restore(snapshot);

        AccountChangesAtIndex accountChanges = slice.GetAccountChanges(TestItem.AddressA)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountChanges.BalanceChange!.Value.Value, Is.EqualTo((UInt256)10));
            Assert.That(accountChanges.NonceChange!.Value.Value, Is.EqualTo(1u));
            Assert.That(accountChanges.CodeChange!.Value.Code, Is.EqualTo(codeBeforeSnapshot));

            Assert.That(accountChanges.TryGetStorageChange(slot, out StorageChange? slotChange), Is.True);
            Assert.That(slotChange!.Value.Value, Is.EqualTo((UInt256)11));
        }
    }

    [Test]
    public void Restore_after_delete_account_restores_within_block_change_entries()
    {
        UInt256 slot = 9;
        BlockAccessListAtIndex slice = new() { Index = 1 };
        slice.AddBalanceChange(TestItem.AddressA, before: 0, after: 50);
        slice.AddNonceChange(TestItem.AddressA, 3);
        slice.AddCodeChange(TestItem.AddressA, before: [], after: new byte[] { 0x60, 0x01 });
        slice.AddStorageChange(TestItem.AddressA, slot, before: 0, after: 77);

        int snapshot = slice.TakeSnapshot();

        slice.DeleteAccount(TestItem.AddressA, oldBalance: 50);
        slice.Restore(snapshot);

        AccountChangesAtIndex accountChanges = slice.GetAccountChanges(TestItem.AddressA)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountChanges.BalanceChange!.Value.Value, Is.EqualTo((UInt256)50));
            Assert.That(accountChanges.NonceChange!.Value.Value, Is.EqualTo(3u));
            Assert.That(accountChanges.CodeChange!.Value.Code, Is.EqualTo(new byte[] { 0x60, 0x01 }));

            Assert.That(accountChanges.TryGetStorageChange(slot, out StorageChange? slotChange), Is.True);
            Assert.That(slotChange!.Value.Value, Is.EqualTo((UInt256)77));
        }
    }

    [Test]
    public void Restore_to_zero_clears_every_change_made_in_the_slice()
    {
        // Snapshot at 0 represents "before any mutation".
        UInt256 slot = 4;
        BlockAccessListAtIndex slice = new() { Index = 1 };

        int empty = slice.TakeSnapshot();

        slice.AddBalanceChange(TestItem.AddressA, before: 0, after: 1);
        slice.AddNonceChange(TestItem.AddressA, 9);
        slice.AddStorageChange(TestItem.AddressA, slot, before: 0, after: 0x42);

        slice.Restore(empty);

        AccountChangesAtIndex? accountChanges = slice.GetAccountChanges(TestItem.AddressA);
        Assert.That(accountChanges, Is.Not.Null, "the AccountChangesAtIndex entry persists; only the change fields revert");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accountChanges!.BalanceChange, Is.Null);
            Assert.That(accountChanges.NonceChange, Is.Null);
            Assert.That(accountChanges.TryGetStorageChange(slot, out _), Is.False);
        }
    }
}
