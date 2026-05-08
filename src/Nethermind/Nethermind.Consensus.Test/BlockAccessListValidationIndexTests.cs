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

public class BlockAccessListValidationIndexTests
{
    private BlockAccessListValidationIndex.AddressIndex _addressIndex = null!;

    [SetUp]
    public void SetUp() => _addressIndex = new();

    [Test]
    public void ChangesEqual_matches_exact_per_index_deltas()
    {
        BlockAccessList suggested = CreateBaselineBal();
        BlockAccessList generated = CreateBaselineBal();

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
        BlockAccessList suggested = Bal(
            Account(TestItem.AddressB, nonce: new NonceChange(1, 2)),
            Account(TestItem.AddressA, balance: new BalanceChange(1, 1)));
        BlockAccessList generated = Bal(
            Account(TestItem.AddressA, balance: new BalanceChange(1, 1)),
            Account(TestItem.AddressB, nonce: new NonceChange(1, 2)));

        BlockAccessListValidationIndex generatedIndex = BuildPair(suggested, generated, txCount: 1, out BlockAccessListValidationIndex suggestedIndex);

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.True);
    }

    [Test]
    public void ChangesEqual_matches_storage_by_account_and_slot_when_insertion_order_differs()
    {
        BlockAccessList suggested = Bal(
            Account(TestItem.AddressB,
                (9, new StorageChange(1, 90)),
                (1, new StorageChange(1, 10))),
            Account(TestItem.AddressA,
                (3, new StorageChange(1, 30))));
        BlockAccessList generated = Bal(
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
        AccountChanges[] suggestedAccounts = new AccountChanges[9];
        AccountChanges[] generatedAccounts = new AccountChanges[9];

        for (int i = 0; i < suggestedAccounts.Length; i++)
        {
            Address address = TestItem.Addresses[i];
            UInt256 value = (UInt256)(i + 1);
            suggestedAccounts[i] = Account(address, balance: new BalanceChange(1, value));
            generatedAccounts[suggestedAccounts.Length - i - 1] = Account(address, balance: new BalanceChange(1, value));
        }

        BlockAccessList suggested = Bal(suggestedAccounts);
        BlockAccessList generated = Bal(generatedAccounts);

        BlockAccessListValidationIndex generatedIndex = BuildPair(suggested, generated, txCount: 1, out BlockAccessListValidationIndex suggestedIndex);

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.True);
    }

    [Test]
    public void ChangesEqual_generated_row_overflow_does_not_corrupt_later_rows()
    {
        BlockAccessList suggested = Bal(
            Account(TestItem.AddressA, balance: new BalanceChange(1, 1)),
            Account(TestItem.AddressB, nonce: new NonceChange(2, 2)));
        BlockAccessList generated = Bal(
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
        BlockAccessList suggested = CreateBaselineBal();
        BlockAccessList generated = CreateMismatchedBal(mismatch);

        BlockAccessListValidationIndex generatedIndex = BuildPair(suggested, generated, txCount: 3, out BlockAccessListValidationIndex suggestedIndex);

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, index), Is.False);
    }

    [Test]
    public void ChangesEqual_ignores_read_only_accounts()
    {
        BlockAccessList suggested = Bal(
            Build.An.AccountChanges
                .WithAddress(TestItem.AddressA)
                .WithStorageReads(1, 2)
                .TestObject);
        BlockAccessList generated = new();

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
        BlockAccessList suggested = Bal(Account(TestItem.AddressA, balance: new BalanceChange(3, 1)));
        BlockAccessList generated = Bal(Account(TestItem.AddressA, balance: new BalanceChange(3, 1)));

        BlockAccessListValidationIndex generatedIndex = BuildPair(suggested, generated, txCount: 2, out BlockAccessListValidationIndex suggestedIndex);

        Assert.Multiple(() =>
        {
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 3), Is.True);
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 4), Is.False);
        });
    }

    private BlockAccessListValidationIndex BuildPair(
        BlockAccessList suggested,
        BlockAccessList generated,
        int txCount,
        out BlockAccessListValidationIndex suggestedIndex)
    {
        suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount, _addressIndex);
        BlockAccessListValidationIndex generatedIndex = new(txCount, _addressIndex, suggestedIndex);
        generatedIndex.Add(generated);
        return generatedIndex;
    }

    private static AccountChanges Account(
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

    private static AccountChanges Account(Address address, params (UInt256 slot, StorageChange change)[] storage)
    {
        AccountChangesBuilder b = Build.An.AccountChanges.WithAddress(address);
        foreach ((UInt256 slot, StorageChange ch) in storage)
        {
            b = b.WithStorageChanges(slot, ch);
        }
        return b.TestObject;
    }

    private static BlockAccessList Bal(params AccountChanges[] accounts) =>
        Build.A.BlockAccessList.WithAccountChanges(accounts).TestObject;

    private static BlockAccessList CreateBaselineBal(
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

    private static BlockAccessList CreateMismatchedBal(ValidationIndexMismatch mismatch) =>
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
