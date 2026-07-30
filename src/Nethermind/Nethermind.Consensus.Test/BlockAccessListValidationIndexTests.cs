// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class BlockAccessListValidationIndexTests
{
    private BlockAccessListValidationIndex.AddressIndex _addressIndex = null!;

    [SetUp]
    public void SetUp() => _addressIndex = new();

    [Test]
    public void ChangesEqual_matches_exact_per_index_deltas()
    {
        ReadOnlyBlockAccessList suggested = CreateBaselineBal();
        ReadOnlyBlockAccessList generated = CreateBaselineBal();

        BlockAccessListValidationIndex generatedIndex = BuildPair(suggested, generated, txCount: 3, out BlockAccessListValidationIndex suggestedIndex);

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
            Account(TestItem.AddressB, nonce: new NonceChange(1, 2)),
            Account(TestItem.AddressA, balance: new BalanceChange(1, 1)));
        ReadOnlyBlockAccessList generated = Bal(
            Account(TestItem.AddressA, balance: new BalanceChange(1, 1)),
            Account(TestItem.AddressB, nonce: new NonceChange(1, 2)));

        BlockAccessListValidationIndex generatedIndex = BuildPair(suggested, generated, txCount: 1, out BlockAccessListValidationIndex suggestedIndex);

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.True);
    }

    [Test]
    public void ChangesEqual_matches_storage_by_account_and_slot_when_insertion_order_differs()
    {
        ReadOnlyBlockAccessList suggested = Bal(
            Account(TestItem.AddressB,
                (9, new StorageChange(1, 90)),
                (1, new StorageChange(1, 10))),
            Account(TestItem.AddressA,
                (3, new StorageChange(1, 30))));
        ReadOnlyBlockAccessList generated = Bal(
            Account(TestItem.AddressA,
                (3, new StorageChange(1, 30))),
            Account(TestItem.AddressB,
                (1, new StorageChange(1, 10)),
                (9, new StorageChange(1, 90))));

        BlockAccessListValidationIndex generatedIndex = BuildPair(suggested, generated, txCount: 1, out BlockAccessListValidationIndex suggestedIndex);

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.True);
    }

    [Test]
    public void ChangesEqual_sorts_large_rows_deterministically()
    {
        ReadOnlyAccountChanges[] suggestedAccounts = new ReadOnlyAccountChanges[9];
        ReadOnlyAccountChanges[] generatedAccounts = new ReadOnlyAccountChanges[9];

        for (int i = 0; i < suggestedAccounts.Length; i++)
        {
            Address address = TestItem.Addresses[i];
            UInt256 value = (UInt256)(i + 1);
            suggestedAccounts[i] = Account(address, balance: new BalanceChange(1, value));
            generatedAccounts[suggestedAccounts.Length - i - 1] = Account(address, balance: new BalanceChange(1, value));
        }

        ReadOnlyBlockAccessList suggested = Bal(suggestedAccounts);
        ReadOnlyBlockAccessList generated = Bal(generatedAccounts);

        BlockAccessListValidationIndex generatedIndex = BuildPair(suggested, generated, txCount: 1, out BlockAccessListValidationIndex suggestedIndex);

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.True);
    }

    [Test]
    public void ChangesEqual_generated_row_overflow_does_not_corrupt_later_rows()
    {
        ReadOnlyBlockAccessList suggested = Bal(
            Account(TestItem.AddressA, balance: new BalanceChange(1, 1)),
            Account(TestItem.AddressB, nonce: new NonceChange(2, 2)));
        ReadOnlyBlockAccessList generated = Bal(
            Account(TestItem.AddressA, balance: new BalanceChange(1, 1)),
            Account(TestItem.AddressC, balance: new BalanceChange(1, 3)),
            Account(TestItem.AddressB, nonce: new NonceChange(2, 2)));

        BlockAccessListValidationIndex generatedIndex = BuildPair(suggested, generated, txCount: 2, out BlockAccessListValidationIndex suggestedIndex);

        Assert.Multiple(() =>
        {
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.False);
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 2), Is.True);
        });
    }

    [TestCase(ValidationIndexMismatch.MissingBalance, 0u)]
    [TestCase(ValidationIndexMismatch.SurplusBalance, 1u)]
    [TestCase(ValidationIndexMismatch.IncorrectBalance, 0u)]
    [TestCase(ValidationIndexMismatch.IncorrectNonce, 1u)]
    [TestCase(ValidationIndexMismatch.IncorrectCode, 2u)]
    [TestCase(ValidationIndexMismatch.IncorrectStorage, 2u)]
    public void ChangesEqual_rejects_indexed_change_mismatches(ValidationIndexMismatch mismatch, uint index)
    {
        ReadOnlyBlockAccessList suggested = CreateBaselineBal();
        ReadOnlyBlockAccessList generated = CreateMismatchedBal(mismatch);

        BlockAccessListValidationIndex generatedIndex = BuildPair(suggested, generated, txCount: 3, out BlockAccessListValidationIndex suggestedIndex);

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, index), Is.False);
    }

    [Test]
    public void ChangesEqual_does_not_detect_surplus_read_only_account_in_generated()
    {
        // Read-only accounts produce no lane rows, so the column-index can't see a surplus
        // one — ChangesEqual returns true. The manager's _hasGeneratedRequiredReadAccountMismatch
        // flag (set in RegisterGeneratedSlice) catches it instead.
        ReadOnlyBlockAccessList suggested = Bal(
            Account(TestItem.AddressA, balance: new BalanceChange(1, 1)));

        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 1, _addressIndex);
        BlockAccessListValidationIndex generatedIndex = new(1, _addressIndex, suggestedIndex, suggested.TotalStorageReads, suggested.TotalStorageChangeEvents);

        // Generated slice: AddressA matches, AddressB has only a read.
        BlockAccessListAtIndex slice = new() { Index = 1 };
        slice.AddBalanceChange(TestItem.AddressA, before: 2, after: 1);
        slice.AddAccountRead(TestItem.AddressB);
        generatedIndex.Add(slice);

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.True,
            "ChangesEqual is intentionally blind to read-only surplus accounts; the manager-side flag covers it.");
    }

    [Test]
    public void ChangesEqual_ignores_read_only_accounts()
    {
        ReadOnlyBlockAccessList suggested = Bal(
            Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageReads(1, 2)
                .TestObject);
        ReadOnlyBlockAccessList generated = new();

        BlockAccessListValidationIndex generatedIndex = BuildPair(suggested, generated, txCount: 1, out BlockAccessListValidationIndex suggestedIndex);

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
        ReadOnlyBlockAccessList suggested = Bal(Account(TestItem.AddressA, balance: new BalanceChange(3, 1)));
        ReadOnlyBlockAccessList generated = Bal(Account(TestItem.AddressA, balance: new BalanceChange(3, 1)));

        BlockAccessListValidationIndex generatedIndex = BuildPair(suggested, generated, txCount: 2, out BlockAccessListValidationIndex suggestedIndex);

        Assert.Multiple(() =>
        {
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 3), Is.True);
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 4), Is.False);
        });
    }

    private BlockAccessListValidationIndex BuildPair(
        ReadOnlyBlockAccessList suggested,
        ReadOnlyBlockAccessList generated,
        int txCount,
        out BlockAccessListValidationIndex suggestedIndex)
    {
        suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount, _addressIndex);
        BlockAccessListValidationIndex generatedIndex = new(txCount, _addressIndex, suggestedIndex, suggested.TotalStorageReads, suggested.TotalStorageChangeEvents);
        // BlockAccessListValidationIndex.Add only accepts per-tx slices (one BlockAccessListAtIndex
        // per block-access index). Tests phrase the generated side as a full ReadOnlyBlockAccessList
        // for symmetry with the suggested side, so we shred it back into per-index slices here.
        SortedDictionary<uint, BlockAccessListAtIndex> slicesByIndex = [];
        foreach (ReadOnlyAccountChanges acc in generated.AccountChanges)
        {
            foreach (BalanceChange bc in acc.BalanceChanges)
                GetSlice(slicesByIndex, bc.Index).AddBalanceChange(acc.Address, before: bc.Value + UInt256.One, after: bc.Value);
            foreach (NonceChange nc in acc.NonceChanges)
                GetSlice(slicesByIndex, nc.Index).AddNonceChange(acc.Address, nc.Value);
            foreach (CodeChange cc in acc.CodeChanges)
                GetSlice(slicesByIndex, cc.Index).AddCodeChange(acc.Address, before: [], after: cc.Code);
            foreach (ReadOnlySlotChanges slot in acc.StorageChanges)
            {
                foreach (StorageChange ch in slot.Changes)
                {
                    UInt256 value = ToUInt256(ch.Value);
                    GetSlice(slicesByIndex, ch.Index).AddStorageChange(acc.Address, slot.Key, before: value + UInt256.One, after: value);
                }
            }
        }
        foreach (BlockAccessListAtIndex slice in slicesByIndex.Values) generatedIndex.Add(slice);
        return generatedIndex;

        static BlockAccessListAtIndex GetSlice(SortedDictionary<uint, BlockAccessListAtIndex> slices, uint index)
        {
            if (!slices.TryGetValue(index, out BlockAccessListAtIndex? slice))
            {
                slice = new() { Index = index };
                slices[index] = slice;
            }
            return slice;
        }

        static UInt256 ToUInt256(EvmWord beValue)
        {
            EvmWord leBytes = beValue.ByteSwap();
            return Unsafe.As<EvmWord, UInt256>(ref leBytes);
        }
    }

    private static ReadOnlyAccountChanges Account(
        Address address,
        BalanceChange? balance = null,
        NonceChange? nonce = null,
        CodeChange? code = null,
        (UInt256 slot, StorageChange change)? storage = null)
    {
        AccountChangesBuilder b = Build.An.AccountChanges.WithAddress(address);
        if (balance is { } bal) b = b.WithBalanceChanges(bal);
        if (nonce is { } non) b = b.WithNonceChanges(non);
        if (code is { } cod) b = b.WithCodeChanges(cod);
        if (storage is { } st) b = b.WithStorageChanges(st.slot, st.change);
        return b.TestObject;
    }

    private static ReadOnlyAccountChanges Account(Address address, params (UInt256 slot, StorageChange change)[] storage)
    {
        AccountChangesBuilder b = Build.An.AccountChanges.WithAddress(address);
        foreach ((UInt256 slot, StorageChange ch) in storage)
        {
            b = b.WithStorageChanges(slot, ch);
        }
        return b.TestObject;
    }

    private static ReadOnlyBlockAccessList Bal(params ReadOnlyAccountChanges[] accounts) =>
        Build.A.BlockAccessList.WithAccountChanges(accounts).TestObject;

    private static ReadOnlyBlockAccessList CreateBaselineBal(
        UInt256? balance = null,
        ulong nonce = 7,
        byte[]? code = null,
        UInt256? storageValue = null)
    {
        UInt256 actualBalance = balance ?? 11;
        UInt256 actualStorageValue = storageValue ?? 13;
        byte[] actualCode = code ?? [1, 2, 3];

        return Bal(
            Account(TestItem.AddressA, balance: new BalanceChange(0, actualBalance)),
            Account(TestItem.AddressB, nonce: new NonceChange(1, nonce)),
            Account(TestItem.AddressC, code: new CodeChange(2, actualCode)),
            Account(TestItem.AddressD, storage: (17, new StorageChange(2, actualStorageValue))),
            Account(TestItem.AddressE, balance: new BalanceChange(4, 19)));
    }

    private static ReadOnlyBlockAccessList CreateMismatchedBal(ValidationIndexMismatch mismatch) =>
        mismatch switch
        {
            ValidationIndexMismatch.MissingBalance => Bal(
                Account(TestItem.AddressB, nonce: new NonceChange(1, 7)),
                Account(TestItem.AddressC, code: new CodeChange(2, [1, 2, 3])),
                Account(TestItem.AddressD, storage: (17, new StorageChange(2, 13))),
                Account(TestItem.AddressE, balance: new BalanceChange(4, 19))),
            ValidationIndexMismatch.SurplusBalance => Bal(
                Account(TestItem.AddressF, balance: new BalanceChange(1, 1)),
                Account(TestItem.AddressA, balance: new BalanceChange(0, 11)),
                Account(TestItem.AddressB, nonce: new NonceChange(1, 7)),
                Account(TestItem.AddressC, code: new CodeChange(2, [1, 2, 3])),
                Account(TestItem.AddressD, storage: (17, new StorageChange(2, 13))),
                Account(TestItem.AddressE, balance: new BalanceChange(4, 19))),
            ValidationIndexMismatch.IncorrectBalance => CreateBaselineBal(balance: 12),
            ValidationIndexMismatch.IncorrectNonce => CreateBaselineBal(nonce: 8),
            ValidationIndexMismatch.IncorrectCode => CreateBaselineBal(code: [3, 2, 1]),
            ValidationIndexMismatch.IncorrectStorage => CreateBaselineBal(storageValue: 14),
            _ => CreateBaselineBal()
        };

    public enum ValidationIndexMismatch
    {
        MissingBalance,
        SurplusBalance,
        IncorrectBalance,
        IncorrectNonce,
        IncorrectCode,
        IncorrectStorage
    }
}
