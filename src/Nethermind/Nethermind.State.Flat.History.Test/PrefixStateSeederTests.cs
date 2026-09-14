// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State.Flat.History.Changesets;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class PrefixStateSeederTests
{
    private static readonly IReleaseSpec Spec = Prague.Instance;
    private static readonly byte[] Code = [0x60, 0x00, 0x60, 0x00, 0x55];
    private static readonly StorageCell SlotOne = new(TestItem.AddressA, 1);
    private static readonly StorageCell SlotTwo = new(TestItem.AddressA, 2);

    private IWorldState _state = null!;
    private IDisposable _scope = null!;
    private MidBlockOverlay _overlay = null!;

    [SetUp]
    public void SetUp()
    {
        _state = TestWorldStateFactory.CreateForTest();
        _scope = _state.BeginScope(IWorldState.PreGenesis);
        _state.CreateAccount(TestItem.AddressA, 100, 5);
        _state.Set(SlotOne, 0x11);
        _state.Set(SlotTwo, 0x22);
        _state.CreateAccount(TestItem.AddressB, 1, 1);
        _state.InsertCode(TestItem.AddressB, Code, Spec);
        _state.Commit(Spec);
        _overlay = new MidBlockOverlay();
        _overlay.Reset(1);
    }

    [TearDown]
    public void TearDown() => _scope.Dispose();

    [Test]
    public void AChangedField_IsApplied_AndAnUntouchedOneKept()
    {
        Fold(0, c => c.Balance(TestItem.AddressA, 42));

        bool applied = PrefixStateSeeder.TryApply(_overlay, _state, Spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied, Is.True);
            Assert.That(_state.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)42));
            Assert.That(_state.GetNonce(TestItem.AddressA), Is.EqualTo(5), "a field no transaction wrote keeps the value the previous block holds");
        }
    }

    [Test]
    public void AWrittenSlot_IsApplied_AndAnUntouchedOneKept()
    {
        Fold(0, c => c.Storage(SlotOne, [0x77]));

        PrefixStateSeeder.TryApply(_overlay, _state, Spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(Slot(SlotOne), Is.EqualTo((UInt256)0x77));
            Assert.That(Slot(SlotTwo), Is.EqualTo((UInt256)0x22));
        }
    }

    [Test]
    public void ADestroyedAccount_IsGoneWithItsSlots()
    {
        Fold(0, c => c.Deleted(TestItem.AddressA));

        PrefixStateSeeder.TryApply(_overlay, _state, Spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_state.AccountExists(TestItem.AddressA), Is.False);
            Assert.That(Slot(SlotTwo), Is.EqualTo(UInt256.Zero), "the slot the block never touched went with the account");
        }
    }

    [Test]
    public void AnAccountRecreatedInOneTransaction_KeepsItsFieldsAndLosesItsSlots()
    {
        Fold(0, c => c.Storage(SlotOne, [0x77]));
        Fold(1, c =>
        {
            c.StorageCleared(TestItem.AddressA);
            c.Code(TestItem.AddressA, Code);
        });
        Fold(2, c => c.Storage(SlotOne, [0x99]));

        PrefixStateSeeder.TryApply(_overlay, _state, Spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_state.AccountExists(TestItem.AddressA), Is.True);
            Assert.That(_state.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)100), "the create did not report a balance, so the account keeps the one it had");
            Assert.That(_state.GetCode(TestItem.AddressA), Is.EqualTo(Code));
            Assert.That(Slot(SlotTwo), Is.EqualTo(UInt256.Zero), "the create wiped every slot the account held");
            Assert.That(Slot(SlotOne), Is.EqualTo((UInt256)0x99), "a slot written after the wipe survives it");
        }
    }

    [Test]
    public void AnAccountRecreatedAfterItsDestruction_StartsFromTheEmptyAccount()
    {
        Fold(0, c => c.Deleted(TestItem.AddressA));
        Fold(1, c => c.Balance(TestItem.AddressA, 7));

        PrefixStateSeeder.TryApply(_overlay, _state, Spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_state.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)7));
            Assert.That(_state.GetNonce(TestItem.AddressA), Is.Zero, "the nonce of the recreated account is zero, not the five it carried before the block");
            Assert.That(Slot(SlotOne), Is.EqualTo(UInt256.Zero));
        }
    }

    [Test]
    public void AnAccountTheBlockCreated_IsCreated()
    {
        Fold(0, c =>
        {
            c.Balance(TestItem.AddressC, 3);
            c.Nonce(TestItem.AddressC, 1);
            c.Code(TestItem.AddressC, Code);
        });

        PrefixStateSeeder.TryApply(_overlay, _state, Spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_state.GetBalance(TestItem.AddressC), Is.EqualTo((UInt256)3));
            Assert.That(_state.GetNonce(TestItem.AddressC), Is.EqualTo(1));
            Assert.That(_state.GetCode(TestItem.AddressC), Is.EqualTo(Code), "code is content-addressed, so a hash the node once executed resolves without the bytes being in the row");
        }
    }

    [Test]
    public void CodeTheNodeNeverSaw_RefusesTheSeed_AndLeavesTheStateAlone()
    {
        Fold(0, c =>
        {
            c.Balance(TestItem.AddressA, 42);
            c.Code(TestItem.AddressA, [0xfe, 0xed]);
        });

        bool applied = PrefixStateSeeder.TryApply(_overlay, _state, Spec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(applied, Is.False);
            Assert.That(_state.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)100), "a seed that cannot be completed must not be half applied");
        }
    }

    private UInt256 Slot(in StorageCell cell)
    {
        _state.Get(cell, out UInt256 value);
        return value;
    }

    private void Fold(ushort transactionIndex, Action<ChangesetCollector> writes)
    {
        ChangesetCollector collector = new();
        writes(collector);
        _overlay.Fold(transactionIndex, collector.Pack());
        collector.Release();
    }
}
