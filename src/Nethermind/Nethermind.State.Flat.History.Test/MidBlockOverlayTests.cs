// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
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

        _overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.Balance, Is.EqualTo((UInt256)9));
            Assert.That(_overlay.Folded, Is.EqualTo(2));
        }
    }

    [Test]
    public void AFieldNoTransactionWrote_IsLeftToThePreviousBlock()
    {
        Fold(0, c => c.Balance(TestItem.AddressA, 5));

        _overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.Nonce, Is.Null);
            Assert.That(account.Emptied, Is.False);
        }
    }

    [Test]
    public void ADeletedAccount_StopsExisting()
    {
        Fold(0, c => c.Balance(TestItem.AddressA, 5));
        Fold(1, c => c.Deleted(TestItem.AddressA));

        _overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.Exists, Is.False);
            Assert.That(account.Emptied, Is.True);
            Assert.That(account.Balance, Is.Null, "the balance the earlier transaction wrote went with the account");
        }
    }

    [Test]
    public void AnAccountRecreatedAfterItsDeletion_TakesItsUnwrittenFieldsFromTheEmptyAccount()
    {
        Fold(0, c => c.Deleted(TestItem.AddressA));
        Fold(1, c => c.Balance(TestItem.AddressA, 7));

        _overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.Exists, Is.True);
            Assert.That(account.Emptied, Is.True, "the nonce of the recreated account is zero, not the nonce it carried before the block");
            Assert.That(account.Balance, Is.EqualTo((UInt256)7));
        }
    }

    [Test]
    public void ASlotWrittenByAnEarlierTransaction_IsServedFromTheOverlay()
    {
        Fold(0, c => c.Storage(SlotOne, [0x42]));

        bool found = _overlay.TryGetStorage(SlotOne, out ReadOnlySpan<byte> value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(value.ToArray(), Is.EqualTo(new byte[] { 0x42 }));
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

        bool writtenBeforeTheDestruct = _overlay.TryGetStorage(SlotOne, out ReadOnlySpan<byte> written);
        bool neverWritten = _overlay.TryGetStorage(SlotTwo, out ReadOnlySpan<byte> untouched);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(writtenBeforeTheDestruct, Is.True);
            Assert.That(written.IsEmpty, Is.True);
            Assert.That(neverWritten, Is.True, "a slot the block never touched was destroyed with the rest, so the read must not fall through to the previous block");
            Assert.That(untouched.IsEmpty, Is.True);
        }
    }

    [Test]
    public void ASlotWrittenAfterTheDestruct_SurvivesIt()
    {
        Fold(0, c => c.Storage(SlotOne, [0x42]));
        Fold(1, c => c.Deleted(TestItem.AddressA));
        Fold(2, c => c.Storage(SlotOne, [0x77]));

        _overlay.TryGetStorage(SlotOne, out ReadOnlySpan<byte> value);

        Assert.That(value.ToArray(), Is.EqualTo(new byte[] { 0x77 }));
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

        bool found = _overlay.TryGetStorage(SlotOne, out ReadOnlySpan<byte> value);
        _overlay.TryGetAccount(TestItem.AddressA, out MidBlockOverlay.AccountOverlay account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(value.IsEmpty, Is.True, "the create wiped the slots even though the account itself survived the transaction");
            Assert.That(account.Exists, Is.True);
        }
    }

    [Test]
    public void ASlotOfAnotherAccount_IsNotWipedWithIt()
    {
        StorageCell otherAccount = new(TestItem.AddressB, 1);
        Fold(0, c => c.Storage(otherAccount, [0x42]));
        Fold(1, c => c.Deleted(TestItem.AddressA));

        _overlay.TryGetStorage(otherAccount, out ReadOnlySpan<byte> value);

        Assert.That(value.ToArray(), Is.EqualTo(new byte[] { 0x42 }));
    }

    private void Fold(ushort transactionIndex, Action<ChangesetCollector> writes)
    {
        ChangesetCollector collector = new();
        writes(collector);
        _overlay.Fold(transactionIndex, collector.Pack());
        collector.Release();
    }
}
