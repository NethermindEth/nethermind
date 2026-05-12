// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

/// <summary>
/// Unit tests for the column-oriented validation index used by the fast path in
/// <see cref="BlockAccessListManager.ValidateBlockAccessList"/>. Build the suggested index
/// from a <see cref="ReadOnlyBlockAccessList"/>, push per-tx generated slices via
/// <see cref="BlockAccessListValidationIndex.Add"/>, and assert <c>ChangesEqual</c> matches
/// the right answer per index row.
/// </summary>
public class BlockAccessListValidationIndexTests
{
    private BlockAccessListValidationIndex.AddressIndex _addressIndex = null!;

    [SetUp]
    public void SetUp() => _addressIndex = new();

    [Test]
    public void ChangesEqual_matches_exact_per_index_deltas()
    {
        ReadOnlyBlockAccessList suggested = CreateBaselineBal();
        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 3, _addressIndex);
        BlockAccessListValidationIndex generatedIndex = new(txCount: 3, _addressIndex, suggestedIndex);

        // Mirror the baseline BAL via per-tx slices.
        generatedIndex.Add(Slice(0, (TestItem.AddressA, BalanceAt(11))));
        generatedIndex.Add(Slice(1, (TestItem.AddressB, NonceAt(7))));
        generatedIndex.Add(Slice(2, (TestItem.AddressC, CodeAt([1, 2, 3])),
                                      (TestItem.AddressD, StorageAt((UInt256)17, 13))));
        generatedIndex.Add(Slice(4, (TestItem.AddressE, BalanceAt(19))));

        Assert.Multiple(() =>
        {
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 0), Is.True);
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.True);
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 2), Is.True);
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 3), Is.True);
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 4), Is.True);
        });
    }

    [Test]
    public void ChangesEqual_matches_accounts_by_address_when_insertion_order_differs()
    {
        ReadOnlyBlockAccessList suggested = Bal(
            Account(TestItem.AddressB).WithNonceChanges(new NonceChange(1, 2)).TestObject,
            Account(TestItem.AddressA).WithBalanceChanges(new BalanceChange(1, 1)).TestObject);

        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 1, _addressIndex);
        BlockAccessListValidationIndex generatedIndex = new(1, _addressIndex, suggestedIndex);

        // Generated slice pushes in reverse account order — index must still match.
        generatedIndex.Add(Slice(1,
            (TestItem.AddressA, BalanceAt(1)),
            (TestItem.AddressB, NonceAt(2))));

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.True);
    }

    [Test]
    public void ChangesEqual_matches_storage_by_account_and_slot_when_insertion_order_differs()
    {
        ReadOnlyBlockAccessList suggested = Bal(
            Account(TestItem.AddressB)
                .WithStorageChanges(9, new StorageChange(1, 90))
                .WithStorageChanges(1, new StorageChange(1, 10))
                .TestObject,
            Account(TestItem.AddressA)
                .WithStorageChanges(3, new StorageChange(1, 30))
                .TestObject);

        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 1, _addressIndex);
        BlockAccessListValidationIndex generatedIndex = new(1, _addressIndex, suggestedIndex);

        // Different account + slot insertion order than the suggested BAL.
        generatedIndex.Add(Slice(1,
            (TestItem.AddressA, StorageAt((UInt256)3, 30)),
            (TestItem.AddressB, StorageAt((UInt256)1, 10)),
            (TestItem.AddressB, StorageAt((UInt256)9, 90))));

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.True);
    }

    [Test]
    public void ChangesEqual_sorts_large_rows_deterministically()
    {
        // 9 accounts forces the sort path (size > 8 triggers SortWithScratch / Array.Sort).
        ReadOnlyAccountChanges[] suggestedAccounts = new ReadOnlyAccountChanges[9];
        (Address, ChangeSpec)[] generatedSpecs = new (Address, ChangeSpec)[9];

        for (int i = 0; i < suggestedAccounts.Length; i++)
        {
            Address address = TestItem.Addresses[i];
            UInt256 value = (UInt256)(i + 1);
            suggestedAccounts[i] = Account(address).WithBalanceChanges(new BalanceChange(1, value)).TestObject;
            generatedSpecs[suggestedAccounts.Length - i - 1] = (address, BalanceAt(value));
        }

        ReadOnlyBlockAccessList suggested = Bal(suggestedAccounts);
        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 1, _addressIndex);
        BlockAccessListValidationIndex generatedIndex = new(1, _addressIndex, suggestedIndex);

        generatedIndex.Add(Slice(1, generatedSpecs));

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.True);
    }

    [Test]
    public void ChangesEqual_generated_row_overflow_does_not_corrupt_later_rows()
    {
        ReadOnlyBlockAccessList suggested = Bal(
            Account(TestItem.AddressA).WithBalanceChanges(new BalanceChange(1, 1)).TestObject,
            Account(TestItem.AddressB).WithNonceChanges(new NonceChange(2, 2)).TestObject);

        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 2, _addressIndex);
        BlockAccessListValidationIndex generatedIndex = new(2, _addressIndex, suggestedIndex);

        // Push extra account C at index 1 — overflows row 1's capacity (suggested has only one
        // balance entry at row 1). Row 2 still pushes cleanly.
        generatedIndex.Add(Slice(1,
            (TestItem.AddressA, BalanceAt(1)),
            (TestItem.AddressC, BalanceAt(3))));
        generatedIndex.Add(Slice(2, (TestItem.AddressB, NonceAt(2))));

        Assert.Multiple(() =>
        {
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.False);
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 2), Is.True);
        });
    }

    [Test]
    public void ChangesEqual_rejects_missing_balance_change()
    {
        ReadOnlyBlockAccessList suggested = CreateBaselineBal();
        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 3, _addressIndex);
        BlockAccessListValidationIndex generatedIndex = new(3, _addressIndex, suggestedIndex);

        // Skip AddressA's balance at index 0 — suggested expects it, generated doesn't push.
        generatedIndex.Add(Slice(1, (TestItem.AddressB, NonceAt(7))));
        generatedIndex.Add(Slice(2, (TestItem.AddressC, CodeAt([1, 2, 3])),
                                      (TestItem.AddressD, StorageAt((UInt256)17, 13))));
        generatedIndex.Add(Slice(4, (TestItem.AddressE, BalanceAt(19))));

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 0), Is.False);
    }

    [Test]
    public void ChangesEqual_rejects_incorrect_balance_value()
    {
        ReadOnlyBlockAccessList suggested = CreateBaselineBal();
        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 3, _addressIndex);
        BlockAccessListValidationIndex generatedIndex = new(3, _addressIndex, suggestedIndex);

        // Generated reports a different balance for AddressA at index 0.
        generatedIndex.Add(Slice(0, (TestItem.AddressA, BalanceAt(12))));

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 0), Is.False);
    }

    [Test]
    public void ChangesEqual_rejects_incorrect_nonce_value()
    {
        ReadOnlyBlockAccessList suggested = CreateBaselineBal();
        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 3, _addressIndex);
        BlockAccessListValidationIndex generatedIndex = new(3, _addressIndex, suggestedIndex);

        generatedIndex.Add(Slice(1, (TestItem.AddressB, NonceAt(8))));

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.False);
    }

    [Test]
    public void ChangesEqual_rejects_incorrect_code_value()
    {
        ReadOnlyBlockAccessList suggested = CreateBaselineBal();
        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 3, _addressIndex);
        BlockAccessListValidationIndex generatedIndex = new(3, _addressIndex, suggestedIndex);

        generatedIndex.Add(Slice(2, (TestItem.AddressC, CodeAt([3, 2, 1]))));

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 2), Is.False);
    }

    [Test]
    public void ChangesEqual_rejects_incorrect_storage_value()
    {
        ReadOnlyBlockAccessList suggested = CreateBaselineBal();
        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 3, _addressIndex);
        BlockAccessListValidationIndex generatedIndex = new(3, _addressIndex, suggestedIndex);

        generatedIndex.Add(Slice(2, (TestItem.AddressD, StorageAt((UInt256)17, 14))));

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 2), Is.False);
    }

    [Test]
    public void ChangesEqual_ignores_read_only_accounts()
    {
        ReadOnlyBlockAccessList suggested = Bal(
            Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageReads(1, 2)
                .TestObject);

        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 1, _addressIndex);
        BlockAccessListValidationIndex generatedIndex = new(1, _addressIndex, suggestedIndex);
        // Generated pushes nothing — read-only accounts contribute no rows.

        Assert.Multiple(() =>
        {
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.True);
            Assert.That(suggestedIndex.HasAccount(TestItem.AddressA), Is.True);
            Assert.That(suggestedIndex.HasAccount(TestItem.AddressB), Is.False);
        });
    }

    [Test]
    public void ChangesEqual_supports_post_execution_index()
    {
        ReadOnlyBlockAccessList suggested = Bal(
            Account(TestItem.AddressA).WithBalanceChanges(new BalanceChange(3, 1)).TestObject);

        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 2, _addressIndex);
        BlockAccessListValidationIndex generatedIndex = new(2, _addressIndex, suggestedIndex);

        // index 3 is post-execution for a 2-tx block (lastIndex = txCount + 1).
        generatedIndex.Add(Slice(3, (TestItem.AddressA, BalanceAt(1))));

        Assert.Multiple(() =>
        {
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 3), Is.True);
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 4), Is.False);
        });
    }

    // === helpers ===

    private abstract record ChangeSpec;
    private sealed record BalanceSpec(UInt256 Value) : ChangeSpec;
    private sealed record NonceSpec(ulong Value) : ChangeSpec;
    private sealed record CodeSpec(byte[] Code) : ChangeSpec;
    private sealed record StorageSpec(UInt256 Slot, UInt256 Value) : ChangeSpec;

    private static ChangeSpec BalanceAt(UInt256 value) => new BalanceSpec(value);
    private static ChangeSpec NonceAt(ulong value) => new NonceSpec(value);
    private static ChangeSpec CodeAt(byte[] code) => new CodeSpec(code);
    private static ChangeSpec StorageAt(UInt256 slot, UInt256 value) => new StorageSpec(slot, value);

    /// <summary>Build a single per-tx slice with the given changes at the given index.</summary>
    private static BlockAccessListAtIndex Slice(uint index, params (Address Address, ChangeSpec Change)[] entries)
    {
        BlockAccessListAtIndex slice = new() { Index = index };
        foreach ((Address address, ChangeSpec change) in entries)
        {
            switch (change)
            {
                case BalanceSpec b:
                    // AddBalanceChange compares before/after and skips no-ops; pick a "before"
                    // that differs and matches the test's intent (value is the post-tx value).
                    slice.AddBalanceChange(address, before: b.Value + UInt256.One, after: b.Value);
                    break;
                case NonceSpec n:
                    slice.AddNonceChange(address, n.Value);
                    break;
                case CodeSpec c:
                    slice.AddCodeChange(address, before: [], after: c.Code);
                    break;
                case StorageSpec s:
                    slice.AddStorageChange(address, s.Slot, before: s.Value + UInt256.One, after: s.Value);
                    break;
            }
        }
        return slice;
    }

    private static AccountChangesBuilder Account(Address address) =>
        Build.An.AccountChanges.WithAddress(address);

    private static ReadOnlyBlockAccessList Bal(params ReadOnlyAccountChanges[] accounts) =>
        Build.A.BlockAccessList.WithAccountChanges(accounts).TestObject;

    private static ReadOnlyBlockAccessList CreateBaselineBal() => Bal(
        Account(TestItem.AddressA).WithBalanceChanges(new BalanceChange(0, 11)).TestObject,
        Account(TestItem.AddressB).WithNonceChanges(new NonceChange(1, 7)).TestObject,
        Account(TestItem.AddressC).WithCodeChanges(new CodeChange(2, [1, 2, 3])).TestObject,
        Account(TestItem.AddressD).WithStorageChanges(17, new StorageChange(2, 13)).TestObject,
        Account(TestItem.AddressE).WithBalanceChanges(new BalanceChange(4, 19)).TestObject);
}
