// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class MidBlockOverlayTests
{
    private static readonly StorageCell SlotOne = new(TestItem.AddressA, 1);
    private static readonly StorageCell SlotTwo = new(TestItem.AddressA, 2);

    private MidBlockOverlay _overlay = null!;

    [SetUp]
    public void SetUp()
    {
        _overlay = new MidBlockOverlay();
        _overlay.Reset(100);
    }

    [Test]
    public void TheLastWriteOfTheFoldedPrefix_Wins()
    {
        Fold(0, c => c.Balance(TestItem.AddressA, 5));
        Fold(1, c => c.Balance(TestItem.AddressA, 9));

        _overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay? account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(account!.Balance, Is.EqualTo((UInt256)9));
            Assert.That(_overlay.Folded, Is.EqualTo(2));
        }
    }

    [Test]
    public void AFieldNoTransactionWrote_IsLeftToThePreviousBlock()
    {
        Fold(0, c => c.Balance(TestItem.AddressA, 5));

        _overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay? account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(account!.Nonce, Is.Null);
            Assert.That(account!.Emptied, Is.False);
        }
    }

    [Test]
    public void ADeletedAccount_StopsExisting()
    {
        Fold(0, c => c.Balance(TestItem.AddressA, 5));
        Fold(1, c => c.Deleted(TestItem.AddressA));

        _overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay? account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(account!.Exists, Is.False);
            Assert.That(account!.Emptied, Is.True);
            Assert.That(account!.Balance, Is.Null, "the balance the earlier transaction wrote went with the account");
        }
    }

    [Test]
    public void AnAccountRecreatedAfterItsDeletion_TakesItsUnwrittenFieldsFromTheEmptyAccount()
    {
        Fold(0, c => c.Deleted(TestItem.AddressA));
        Fold(1, c => c.Balance(TestItem.AddressA, 7));

        _overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay? account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(account!.Exists, Is.True);
            Assert.That(account!.Emptied, Is.True, "the nonce of the recreated account is zero, not the nonce it carried before the block");
            Assert.That(account!.Balance, Is.EqualTo((UInt256)7));
        }
    }

    [Test]
    public void ASlotWrittenByAnEarlierTransaction_IsServedFromTheOverlay()
    {
        Fold(0, c => c.Storage(SlotOne, [0x42]));

        bool found = _overlay.TryGetStorage(SlotOne, out UInt256 value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(value, Is.EqualTo((UInt256)0x42));
        }
    }

    [Test]
    public void ASlotNoTransactionWrote_Misses() =>
        Assert.That(_overlay.TryGetStorage(SlotOne, out _), Is.False, "a miss is what sends the read to the state as of the previous block");

    [Test]
    public void EverySlotOfADestroyedAccount_ReadsZero()
    {
        Fold(0, c => c.Storage(SlotOne, [0x42]));
        Fold(1, c => c.Deleted(TestItem.AddressA));

        bool writtenBeforeTheDestruct = _overlay.TryGetStorage(SlotOne, out UInt256 written);
        bool neverWritten = _overlay.TryGetStorage(SlotTwo, out UInt256 untouched);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(writtenBeforeTheDestruct, Is.True);
            Assert.That(written, Is.EqualTo(UInt256.Zero));
            Assert.That(neverWritten, Is.True, "a slot the block never touched was destroyed with the rest, so the read must not fall through to the previous block");
            Assert.That(untouched, Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public void ASlotWrittenAfterTheDestruct_SurvivesIt()
    {
        Fold(0, c => c.Storage(SlotOne, [0x42]));
        Fold(1, c => c.Deleted(TestItem.AddressA));
        Fold(2, c => c.Storage(SlotOne, [0x77]));

        _overlay.TryGetStorage(SlotOne, out UInt256 value);

        Assert.That(value, Is.EqualTo((UInt256)0x77));
    }

    [Test]
    public void AnAccountRecreatedWithinOneTransaction_LosesItsSlotsWithoutBeingDeleted()
    {
        Fold(0, c => c.Storage(SlotOne, [0x42]));
        Fold(1, c =>
        {
            c.StorageCleared(TestItem.AddressA);
            c.Code(TestItem.AddressA, [0x60, 0x00]);
        });

        bool found = _overlay.TryGetStorage(SlotOne, out UInt256 value);
        _overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay? account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(value, Is.EqualTo(UInt256.Zero), "the create wiped the slots even though the account itself survived the transaction");
            Assert.That(account!.Exists, Is.True);
        }
    }

    [Test]
    public void ASlotOfAnotherAccount_IsNotWipedWithIt()
    {
        StorageCell otherAccount = new(TestItem.AddressB, 1);
        Fold(0, c => c.Storage(otherAccount, [0x42]));
        Fold(1, c => c.Deleted(TestItem.AddressA));

        _overlay.TryGetStorage(otherAccount, out UInt256 value);

        Assert.That(value, Is.EqualTo((UInt256)0x42));
    }

    [Test]
    public void FoldingABlockAgainAfterAReset_AllocatesOnlyItsDistinctAddresses()
    {
        byte[][] rows = Block(transactions: 64, addresses: 16, slots: 8);
        FoldAndReset(_overlay, rows);
        FoldAndReset(_overlay, rows);

        long before = GC.GetAllocatedBytesForCurrentThread();
        FoldAndReset(_overlay, rows);
        long warm = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        MidBlockOverlay fresh = new();
        fresh.Reset(100);
        FoldAndReset(fresh, rows);
        long cold = GC.GetAllocatedBytesForCurrentThread() - before;

        TestContext.Out.WriteLine($"Folding 64 rows of 16 addresses: {cold} bytes into a new overlay, {warm} bytes into a reset one.");
        // One instance per distinct address and fold, an object header and twenty bytes rounded up to the allocation unit.
        Assert.That(warm, Is.LessThanOrEqualTo(16 * 40), "a reset overlay keeps the capacity of its previous fold and shares one address instance between every entry naming it");
    }

    [Test]
    public void FoldingAgainAfterAResetOfALargerFold_SeesNothingOfIt()
    {
        foreach (byte[] row in Block(transactions: 64, addresses: 16, slots: 8)) _overlay.Fold(_overlay.Folded, row);
        Fold(_overlay.Folded, c =>
        {
            c.Deleted(TestItem.AddressA);
            c.StorageCleared(TestItem.AddressB);
        });

        _overlay.Reset(101);
        Fold(0, c => c.Balance(TestItem.AddressA, 3));
        Fold(1, c => c.Balance(TestItem.AddressB, 4));

        _overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay? a);
        _overlay.TryGetAccount(TestItem.AddressB, out MidBlockOverlay.AccountOverlay? b);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_overlay.Block, Is.EqualTo(101UL));
            Assert.That(_overlay.Folded, Is.EqualTo(2));
            Assert.That(a!.Balance, Is.EqualTo((UInt256)3));
            Assert.That(a.Emptied, Is.False, "the account reuses the entry of one the previous fold deleted, and must not inherit the deletion");
            Assert.That(a.Exists, Is.True);
            Assert.That(a.Nonce, Is.Null);
            Assert.That(a.CodeHash, Is.Null);
            Assert.That(b!.StorageClearedAt, Is.EqualTo(MidBlockOverlay.NeverCleared), "nor the wipe of another");
            Assert.That(_overlay.HasStorage(TestItem.AddressA), Is.False);
            Assert.That(_overlay.TryGetStorage(new StorageCell(TestItem.Addresses[0], 0), out _), Is.False, "a slot of the previous fold");
            Assert.That(_overlay.TryGetAccount(TestItem.Addresses[0], out _), Is.False, "an account of the previous fold");
        }
    }

    [Test]
    public void AddressesThatShareTheirInterningKey_StayDistinct()
    {
        // The key mixes the first and last eight bytes; these two differ only in between.
        byte[] first = new byte[Address.Size];
        byte[] second = new byte[Address.Size];
        first[8] = 1;
        second[9] = 1;
        Address one = new(first);
        Address two = new(second);

        Fold(0, c =>
        {
            c.Storage(new StorageCell(one, 1), [0x01]);
            c.Balance(one, 5);
        });
        Fold(1, c =>
        {
            c.Storage(new StorageCell(two, 1), [0x02]);
            c.Deleted(two);
        });
        Fold(2, c => c.Storage(new StorageCell(two, 1), [0x03]));

        bool oneFound = _overlay.TryGetStorage(new StorageCell(one, 1), out UInt256 oneValue);
        bool twoFound = _overlay.TryGetStorage(new StorageCell(two, 1), out UInt256 twoValue);
        _overlay.TryGetAccount(one, out MidBlockOverlay.AccountOverlay? oneAccount);
        _overlay.TryGetAccount(two, out MidBlockOverlay.AccountOverlay? twoAccount);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(oneFound && oneValue == 1, Is.True);
            Assert.That(twoFound && twoValue == 3, Is.True);
            Assert.That(oneAccount!.Balance, Is.EqualTo((UInt256)5));
            Assert.That(oneAccount.Exists, Is.True);
            Assert.That(twoAccount!.Exists, Is.False);
            Assert.That(twoAccount.StorageClearedAt, Is.EqualTo(1));
        }
    }

    [Test]
    public void TheLastWriteOfASlotWrittenByEveryTransaction_Wins()
    {
        byte[][] rows = Block(transactions: 64, addresses: 16, slots: 8);
        for (int i = 0; i < rows.Length; i++) _overlay.Fold((ushort)i, rows[i]);

        // Transaction 63 is the last to write address 15 (63 % 16) and address 0 ((63 + 1) % 16).
        bool found = _overlay.TryGetStorage(new StorageCell(TestItem.Addresses[0], 7), out UInt256 value);

        Assert.That(found && value == 63 + 7, Is.True);
    }

    [Test]
    public void ARentedOverlay_StartsEmpty_WhateverTheReturnedOneHeld()
    {
        MidBlockOverlay used = MidBlockOverlay.Rent(100);
        foreach (byte[] row in Block(transactions: 8, addresses: 4, slots: 2)) used.Fold(used.Folded, row);
        MidBlockOverlay.Return(used);

        MidBlockOverlay rented = MidBlockOverlay.Rent(200);
        try
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(rented.Block, Is.EqualTo(200UL));
                Assert.That(rented.Folded, Is.Zero);
                Assert.That(rented.WriteCount, Is.Zero);
                Assert.That(rented.TryGetAccount(TestItem.Addresses[0], out _), Is.False);
                Assert.That(rented.HasStorage(TestItem.Addresses[0]), Is.False);
            }
        }
        finally
        {
            MidBlockOverlay.Return(rented);
        }
    }

    [Test]
    public void TheSpares_HoldNoMoreThanTheTotalBound()
    {
        // Each overlay holds about 11k entries, under the per-overlay limit, so only the total bound can refuse them.
        byte[][] rows = Block(transactions: 64, addresses: 64, slots: 160);
        MidBlockOverlay[] overlays = new MidBlockOverlay[8];
        for (int i = 0; i < overlays.Length; i++)
        {
            overlays[i] = MidBlockOverlay.Rent(100);
            foreach (byte[] row in rows) overlays[i].Fold(overlays[i].Folded, row);
        }

        foreach (MidBlockOverlay overlay in overlays) MidBlockOverlay.Return(overlay);

        Assert.That(MidBlockOverlay.SpareEntries, Is.InRange(0, MidBlockOverlay.MaxSpareEntries));
    }

    /// <summary>Transaction i writes slots of addresses i and i + 1 (mod <paramref name="addresses"/>), so most keys are
    /// written by several transactions, and every eighth transaction deletes the account it writes.</summary>
    private static byte[][] Block(int transactions, int addresses, int slots)
    {
        byte[][] rows = new byte[transactions][];
        ChangesetCollector collector = new();
        for (int i = 0; i < transactions; i++)
        {
            Address own = TestItem.Addresses[i % addresses];
            Address next = TestItem.Addresses[(i + 1) % addresses];
            collector.Balance(own, (UInt256)(i + 1));
            if (i % 8 == 7) collector.Deleted(own);
            for (int slot = 0; slot < slots; slot++)
            {
                collector.Storage(new StorageCell(own, (UInt256)slot), [(byte)(i + slot)]);
                collector.Storage(new StorageCell(next, (UInt256)slot), [(byte)(i + slot)]);
            }

            rows[i] = collector.Pack().ToArray();
            collector.Release();
        }

        return rows;
    }

    private static void FoldAndReset(MidBlockOverlay overlay, byte[][] rows)
    {
        for (int i = 0; i < rows.Length; i++) overlay.Fold((ushort)i, rows[i]);
        overlay.Reset(100);
    }

    private void Fold(ushort transactionIndex, Action<ChangesetCollector> writes)
    {
        ChangesetCollector collector = new();
        writes(collector);
        _overlay.Fold(transactionIndex, collector.Pack());
        collector.Release();
    }
}
