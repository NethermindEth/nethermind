// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

/// <summary>
/// Regression tests for the prestate-sentinel sort-order fix introduced when BlockAccessIndex
/// widened from int to uint per EIP-7928 (commit 645099785a). Before
/// <see cref="PrestateAwareIndexComparer"/> the new <c>uint.MaxValue</c> sentinel sorted last,
/// breaking the iteration patterns in <see cref="AccountChanges"/> that expected prestate first.
/// </summary>
[TestFixture]
public class AccountChangesPrestateTests
{
    [Test]
    public void GetBalance_at_index_zero_returns_prestate_when_only_prestate_present()
    {
        AccountChanges ac = new(TestItem.AddressA);
        ac.AddBalanceChange(new BalanceChange(Eip7928Constants.PrestateIndex, 100));

        Assert.That(ac.GetBalance(0), Is.EqualTo((UInt256)100));
    }

    [Test]
    public void GetBalance_returns_latest_change_strictly_before_index()
    {
        AccountChanges ac = new(TestItem.AddressA);
        ac.AddBalanceChange(new BalanceChange(Eip7928Constants.PrestateIndex, 50));
        ac.AddBalanceChange(new BalanceChange(0u, 200));
        ac.AddBalanceChange(new BalanceChange(2u, 300));

        Assert.That(ac.GetBalance(0), Is.EqualTo((UInt256)50), "queried before any tx index — returns prestate");
        Assert.That(ac.GetBalance(1), Is.EqualTo((UInt256)200), "queried at 1 — last change before is 0");
        Assert.That(ac.GetBalance(2), Is.EqualTo((UInt256)200), "queried at 2 — change at 2 is excluded by >=");
        Assert.That(ac.GetBalance(3), Is.EqualTo((UInt256)300), "queried at 3 — last change before is 2");
    }

    [Test]
    public void GetNonce_at_index_zero_returns_prestate_when_only_prestate_present()
    {
        AccountChanges ac = new(TestItem.AddressA);
        ac.AddNonceChange(new NonceChange(Eip7928Constants.PrestateIndex, 7));

        Assert.That(ac.GetNonce(0), Is.EqualTo((UInt256)7));
    }

    [Test]
    public void Last_balance_change_is_real_when_post_state_change_recorded()
    {
        // Pattern used by BlockAccessListManager.ApplyStateChanges — writes state only when
        // [^1].Index != PrestateIndex. Verifies prestate doesn't poison the [^1] check.
        AccountChanges ac = new(TestItem.AddressA);
        ac.AddBalanceChange(new BalanceChange(Eip7928Constants.PrestateIndex, 100));
        ac.AddBalanceChange(new BalanceChange(0u, 200));

        Assert.That(ac.BalanceChanges[^1].Index, Is.Not.EqualTo(Eip7928Constants.PrestateIndex));
        Assert.That(ac.BalanceChanges[^1].Index, Is.EqualTo(0u));
        Assert.That(ac.BalanceChanges[^1].Value, Is.EqualTo((UInt256)200));
    }

    [Test]
    public void Last_balance_change_is_prestate_when_only_prestate_recorded()
    {
        AccountChanges ac = new(TestItem.AddressA);
        ac.AddBalanceChange(new BalanceChange(Eip7928Constants.PrestateIndex, 100));

        Assert.That(ac.BalanceChanges[^1].Index, Is.EqualTo(Eip7928Constants.PrestateIndex));
    }

    [Test]
    public void AccountExists_returns_true_when_real_change_at_lower_index()
    {
        // AccountExists iterates ascending and breaks when change.Key >= blockAccessIndex.
        // With prestate sorted first, real changes at index 0 are reached before the break.
        AccountChanges ac = new(TestItem.AddressA);
        ac.AddNonceChange(new NonceChange(Eip7928Constants.PrestateIndex, 0));
        ac.AddNonceChange(new NonceChange(0u, 1));

        Assert.That(ac.AccountExists(1), Is.True);
    }

    [Test]
    public void Slot_get_returns_prestate_value_when_only_prestate_present()
    {
        SlotChanges slot = new(123u);
        slot.AddStorageChange(new StorageChange(Eip7928Constants.PrestateIndex, 0xABu));

        byte[] result = slot.Get(0);
        Assert.That(result, Is.EqualTo(new byte[] { 0xAB }));
    }
}
