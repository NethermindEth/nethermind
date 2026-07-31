// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class KeyedNonceManagerTests
{
    private static readonly IReleaseSpec Spec = Prague.Instance;
    private IWorldState _state = null!;
    private IDisposable _scope = null!;

    [SetUp]
    public void Setup()
    {
        _state = TestWorldStateFactory.CreateForTest();
        _scope = _state.BeginScope(IWorldState.PreGenesis);
        _state.CreateAccount(TestItem.AddressA, 1.Ether);
        _state.CreateAccount(Eip8250Constants.NonceManagerAddress, UInt256.Zero, 1);
        _state.Commit(Spec);
        _state.CommitTree(0);
    }

    [TearDown]
    public void TearDown() => _scope.Dispose();

    [Test]
    public void StorageSlot_is_deterministic_and_distinct_per_sender_and_key()
    {
        StorageCell slotA1 = KeyedNonceManager.StorageSlot(TestItem.AddressA, (UInt256)1);
        StorageCell slotA1Again = KeyedNonceManager.StorageSlot(TestItem.AddressA, (UInt256)1);
        StorageCell slotA2 = KeyedNonceManager.StorageSlot(TestItem.AddressA, (UInt256)2);
        StorageCell slotB1 = KeyedNonceManager.StorageSlot(TestItem.AddressB, (UInt256)1);

        Assert.That(slotA1.Address, Is.EqualTo(Eip8250Constants.NonceManagerAddress));
        Assert.That(slotA1Again.Index, Is.EqualTo(slotA1.Index), "same inputs must yield the same slot");
        Assert.That(slotA2.Index, Is.Not.EqualTo(slotA1.Index), "distinct keys must yield distinct slots");
        Assert.That(slotB1.Index, Is.Not.EqualTo(slotA1.Index), "distinct senders must yield distinct slots");
    }

    [Test]
    public void CurrentNonceSeq_for_key_zero_returns_account_nonce()
    {
        _state.SetNonce(TestItem.AddressA, 7);

        Assert.That(KeyedNonceManager.CurrentNonceSeq(_state, TestItem.AddressA, UInt256.Zero), Is.EqualTo(7UL));
    }

    [Test]
    public void CurrentNonceSeq_for_absent_keyed_slot_is_zero() =>
        Assert.That(KeyedNonceManager.CurrentNonceSeq(_state, TestItem.AddressA, (UInt256)5), Is.EqualTo(0UL));

    [TestCaseSource(nameof(AboveUlongMaxValues))]
    public void CurrentNonceSeq_clamps_slot_value_above_ulong_max(UInt256 storedValue)
    {
        StorageCell slot = KeyedNonceManager.StorageSlot(TestItem.AddressA, (UInt256)5);
        _state.Set(slot, storedValue.ToBigEndian().WithoutLeadingZeros().ToArray());

        Assert.That(KeyedNonceManager.CurrentNonceSeq(_state, TestItem.AddressA, (UInt256)5), Is.EqualTo(ulong.MaxValue));
    }

    private static UInt256[] AboveUlongMaxValues() =>
    [
        (UInt256)ulong.MaxValue + UInt256.One,
        UInt256.MaxValue
    ];

    [Test]
    public void ConsumeNonceSet_with_zero_key_increments_account_nonce()
    {
        _state.SetNonce(TestItem.AddressA, 3);

        KeyedNonceManager.ConsumeNonceSet(_state, TestItem.AddressA, [UInt256.Zero], nonceSeq: 99);

        Assert.That(_state.GetNonce(TestItem.AddressA), Is.EqualTo(4UL), "the account nonce must be incremented, not set to nonceSeq + 1");
    }

    [Test]
    public void ConsumeNonceSet_with_keyed_values_writes_next_seq()
    {
        _state.SetNonce(TestItem.AddressA, 3);

        KeyedNonceManager.ConsumeNonceSet(_state, TestItem.AddressA, [(UInt256)5, (UInt256)9], nonceSeq: 42);

        Assert.That(KeyedNonceManager.CurrentNonceSeq(_state, TestItem.AddressA, (UInt256)5), Is.EqualTo(43UL));
        Assert.That(KeyedNonceManager.CurrentNonceSeq(_state, TestItem.AddressA, (UInt256)9), Is.EqualTo(43UL));
        Assert.That(_state.GetNonce(TestItem.AddressA), Is.EqualTo(3UL), "keyed consumption must not touch the account nonce");
    }

    [Test]
    public void IsFirstUse_transitions_true_to_false_after_consume()
    {
        Assert.That(KeyedNonceManager.IsFirstUse(_state, TestItem.AddressA, (UInt256)7), Is.True);

        KeyedNonceManager.ConsumeNonceSet(_state, TestItem.AddressA, [(UInt256)7], nonceSeq: 0);

        Assert.That(KeyedNonceManager.IsFirstUse(_state, TestItem.AddressA, (UInt256)7), Is.False);
        Assert.That(KeyedNonceManager.CurrentNonceSeq(_state, TestItem.AddressA, (UInt256)7), Is.EqualTo(1UL));
    }

    [Test]
    public void IsFirstUse_for_key_zero_is_false() =>
        Assert.That(KeyedNonceManager.IsFirstUse(_state, TestItem.AddressA, UInt256.Zero), Is.False);
}
