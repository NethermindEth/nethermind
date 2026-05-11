// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.BlockAccessLists;

/// <summary>
/// Regression tests for the prestate-sentinel sort-order fix introduced when BlockAccessIndex
/// widened from int to uint per EIP-7928 (commit 645099785a). Before
/// <see cref="PrestateAwareIndexComparer"/> the new <c>uint.MaxValue</c> sentinel sorted last,
/// breaking the iteration patterns in <see cref="ReadOnlyAccountChanges"/> that expected
/// prestate first.
/// </summary>
[TestFixture]
public class AccountChangesPrestateTests
{
    [Test]
    public void GetBalance_at_index_zero_returns_prestate_when_only_prestate_present()
    {
        ReadOnlyAccountChanges ac = new(TestItem.AddressA);
        ac.LoadPreStateBalance(100);

        Assert.That(ac.GetBalance(0), Is.EqualTo((UInt256)100));
    }

    [Test]
    public void GetBalance_returns_latest_change_strictly_before_index()
    {
        ReadOnlyAccountChanges ac = new(
            TestItem.AddressA,
            [],
            [],
            [new BalanceChange(0u, 200), new BalanceChange(2u, 300)],
            [],
            []);
        ac.LoadPreStateBalance(50);

        Assert.That(ac.GetBalance(0), Is.EqualTo((UInt256)50), "queried before any tx index — returns prestate");
        Assert.That(ac.GetBalance(1), Is.EqualTo((UInt256)200), "queried at 1 — last change before is 0");
        Assert.That(ac.GetBalance(2), Is.EqualTo((UInt256)200), "queried at 2 — change at 2 is excluded by >=");
        Assert.That(ac.GetBalance(3), Is.EqualTo((UInt256)300), "queried at 3 — last change before is 2");
    }

    [Test]
    public void GetNonce_at_index_zero_returns_prestate_when_only_prestate_present()
    {
        ReadOnlyAccountChanges ac = new(TestItem.AddressA);
        ac.LoadPreStateNonce(7);

        Assert.That(ac.GetNonce(0), Is.EqualTo((UInt256)7));
    }

    [Test]
    public void GetCode_returns_empty_when_prestate_code_is_missing()
    {
        ReadOnlyAccountChanges ac = new(TestItem.AddressA);

        Assert.That(ac.GetCode(0), Is.Empty);
    }

    [Test]
    public void GetCodeHash_returns_empty_string_hash_when_prestate_code_is_missing()
    {
        ReadOnlyAccountChanges ac = new(TestItem.AddressA);

        Assert.That(ac.GetCodeHash(0), Is.EqualTo(Keccak.OfAnEmptyString.ValueHash256));
    }

    [Test]
    public void Last_balance_change_is_real_when_post_state_change_recorded()
    {
        // Pattern used by BlockAccessListManager.ApplyStateChanges — writes state only when
        // [^1].Index != PrestateIndex. Verifies prestate doesn't poison the [^1] check.
        ReadOnlyAccountChanges ac = new(
            TestItem.AddressA,
            [],
            [],
            [new BalanceChange(0u, 200)],
            [],
            []);
        ac.LoadPreStateBalance(100);

        Assert.That(ac.BalanceChanges[^1].Index, Is.Not.EqualTo(Eip7928Constants.PrestateIndex));
        Assert.That(ac.BalanceChanges[^1].Index, Is.EqualTo(0u));
        Assert.That(ac.BalanceChanges[^1].Value, Is.EqualTo((UInt256)200));
    }

    [Test]
    public void Last_balance_change_is_prestate_when_only_prestate_recorded()
    {
        ReadOnlyAccountChanges ac = new(TestItem.AddressA);
        ac.LoadPreStateBalance(100);

        Assert.That(ac.BalanceChanges[^1].Index, Is.EqualTo(Eip7928Constants.PrestateIndex));
    }

    [Test]
    public void AccountExists_returns_true_when_real_change_at_lower_index()
    {
        // AccountExists skips the prestate sentinel and checks for any real change at an index
        // strictly less than blockAccessIndex.
        ReadOnlyAccountChanges ac = new(
            TestItem.AddressA,
            [],
            [],
            [],
            [new NonceChange(0u, 1)],
            []);
        ac.LoadPreStateNonce(0);

        Assert.That(ac.AccountExists(1), Is.True);
    }

    [Test]
    public void Slot_get_returns_prestate_value_when_only_prestate_present()
    {
        ReadOnlySlotChanges slot = new(123u);
        slot.LoadPreStateChange(new StorageChange(Eip7928Constants.PrestateIndex, 0xABu));

        Span<byte> buffer = stackalloc byte[32];
        ReadOnlySpan<byte> result = slot.Get(0, buffer);
        Assert.That(result.ToArray(), Is.EqualTo(new byte[] { 0xAB }));
    }
}
