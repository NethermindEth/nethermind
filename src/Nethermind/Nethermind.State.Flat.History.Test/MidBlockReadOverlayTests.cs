// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class MidBlockReadOverlayTests
{
    private static readonly byte[] Code = [0x60, 0x00, 0x55];
    private static readonly Account Parent = new(5, 100, TestItem.KeccakA, TestItem.KeccakB);
    private static readonly StorageCell SlotOne = new(TestItem.AddressA, 1);

    private MidBlockOverlay _overlay = null!;
    private MidBlockReadOverlay _read = null!;

    [SetUp]
    public void SetUp()
    {
        _overlay = new MidBlockOverlay();
        _overlay.Reset(1);
        _read = new MidBlockReadOverlay(_overlay);
    }

    [Test]
    public void AnUntouchedAccount_IsNotAnswered() =>
        Assert.That(_read.TryGetAccount(TestItem.AddressA, Parent, out _), Is.False, "a miss is what sends the read to the parent");

    [Test]
    public void AChangedField_IsLaidOverTheParentsAccount()
    {
        Fold(0, c => c.Balance(TestItem.AddressA, 42));

        _read.TryGetAccount(TestItem.AddressA, Parent, out Account? account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(account!.Balance, Is.EqualTo((UInt256)42));
            Assert.That(account.Nonce, Is.EqualTo(5UL), "a field the prefix left alone is the parent's");
            Assert.That(account.StorageRoot, Is.EqualTo(TestItem.KeccakA), "storage untouched keeps the parent's root, so its slots are still read");
            Assert.That(account.CodeHash, Is.EqualTo(TestItem.KeccakB));
        }
    }

    [Test]
    public void ADestroyedAccount_IsGone()
    {
        Fold(0, c => c.Deleted(TestItem.AddressA));

        bool known = _read.TryGetAccount(TestItem.AddressA, Parent, out Account? account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(known, Is.True);
            Assert.That(account, Is.Null);
        }
    }

    [Test]
    public void ARecreatedAccount_StartsFromTheEmptyAccount()
    {
        Fold(0, c => c.Deleted(TestItem.AddressA));
        Fold(1, c => c.Balance(TestItem.AddressA, 7));

        _read.TryGetAccount(TestItem.AddressA, Parent, out Account? account);
        bool slotKnown = _read.TryGetStorage(TestItem.AddressA, 9, out UInt256 slot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(account!.Balance, Is.EqualTo((UInt256)7));
            Assert.That(account.Nonce, Is.EqualTo(0UL), "not the five the parent carried");
            Assert.That(account.StorageRoot, Is.EqualTo(Keccak.EmptyTreeHash));
            Assert.That(account.CodeHash, Is.EqualTo(Keccak.OfAnEmptyString));
            Assert.That(_read.HasStorage(TestItem.AddressA), Is.True, "the destruction wiped the slots, so the storage tree is not skipped");
            Assert.That(slotKnown, Is.True, "a slot the block never touched went with the account");
            Assert.That(slot, Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public void AWipedAccountThatSurvives_KeepsItsFieldsAndLosesItsStorageRoot()
    {
        Fold(0, c =>
        {
            c.StorageCleared(TestItem.AddressA);
            c.Code(TestItem.AddressA, Code);
        });

        _read.TryGetAccount(TestItem.AddressA, Parent, out Account? account);
        bool slotKnown = _read.TryGetStorage(TestItem.AddressA, 9, out UInt256 slot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(account!.Balance, Is.EqualTo((UInt256)100));
            Assert.That(account.StorageRoot, Is.EqualTo(Keccak.EmptyTreeHash));
            Assert.That(account.CodeHash, Is.EqualTo(Keccak.Compute(Code)));
            Assert.That(slotKnown, Is.True, "every slot of a wiped account is answered, as zero");
            Assert.That(slot, Is.EqualTo(UInt256.Zero));
            Assert.That(_read.HasStorage(TestItem.AddressA), Is.True);
        }
    }

    [Test]
    public void AnAccountWithSlotsWrittenAfterItsWipe_DoesNotReportAnEmptyRoot()
    {
        Fold(0, c =>
        {
            c.StorageCleared(TestItem.AddressA);
            c.Storage(SlotOne, [0x77]);
        });

        _read.TryGetAccount(TestItem.AddressA, Parent, out Account? account);

        Assert.That(account!.StorageRoot, Is.Not.EqualTo(Keccak.EmptyTreeHash),
            "the account holds a slot again; reported empty, a wipe later in the trace would find nothing to clear and these slots would read back instead of zero");
    }

    [Test]
    public void ARecreatedAccountThatWritesSlots_DoesNotReportAnEmptyRoot()
    {
        Fold(0, c => c.Deleted(TestItem.AddressA));
        Fold(1, c =>
        {
            c.Balance(TestItem.AddressA, 7);
            c.Storage(SlotOne, [0x77]);
        });

        _read.TryGetAccount(TestItem.AddressA, Parent, out Account? account);

        Assert.That(account!.StorageRoot, Is.Not.EqualTo(Keccak.EmptyTreeHash),
            "the basis is the empty account, but the prefix has written storage onto it since");
    }

    [Test]
    public void AWrittenSlot_IsAnswered_AndAnUntouchedOneIsNot()
    {
        Fold(0, c => c.Storage(SlotOne, [0x77]));

        bool written = _read.TryGetStorage(TestItem.AddressA, 1, out UInt256 value);
        bool untouched = _read.TryGetStorage(TestItem.AddressA, 2, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.True);
            Assert.That(value, Is.EqualTo((UInt256)0x77));
            Assert.That(untouched, Is.False);
            Assert.That(_read.HasStorage(TestItem.AddressA), Is.True);
            Assert.That(_read.HasStorage(TestItem.AddressB), Is.False);
        }
    }

    private void Fold(ushort transactionIndex, Action<ChangesetCollector> writes)
    {
        ChangesetCollector collector = new();
        writes(collector);
        _overlay.Fold(transactionIndex, collector.Pack());
        collector.Release();
    }
}
