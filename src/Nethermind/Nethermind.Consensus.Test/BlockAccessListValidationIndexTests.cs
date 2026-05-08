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
    [Test]
    public void ChangesEqual_matches_exact_per_index_deltas()
    {
        BlockAccessListValidationIndex.AddressIndex addressIndex = new();
        BlockAccessList suggested = CreateBaselineBal();
        BlockAccessList generated = CreateBaselineBal();

        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 3, addressIndex);
        BlockAccessListValidationIndex generatedIndex = BuildGeneratedIndex(generated, txCount: 3, addressIndex, suggestedIndex);

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
        BlockAccessListValidationIndex.AddressIndex addressIndex = new();
        BlockAccessList suggested = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressB)
                    .WithNonceChanges(new NonceChange(1, 2))
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(1, 1))
                    .TestObject)
            .TestObject;
        BlockAccessList generated = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(1, 1))
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressB)
                    .WithNonceChanges(new NonceChange(1, 2))
                    .TestObject)
            .TestObject;

        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 1, addressIndex);
        BlockAccessListValidationIndex generatedIndex = BuildGeneratedIndex(generated, txCount: 1, addressIndex, suggestedIndex);

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.True);
    }

    [Test]
    public void ChangesEqual_matches_storage_by_account_and_slot_when_insertion_order_differs()
    {
        BlockAccessListValidationIndex.AddressIndex addressIndex = new();
        BlockAccessList suggested = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressB)
                    .WithStorageChanges(9, new StorageChange(1, 90))
                    .WithStorageChanges(1, new StorageChange(1, 10))
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithStorageChanges(3, new StorageChange(1, 30))
                    .TestObject)
            .TestObject;
        BlockAccessList generated = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithStorageChanges(3, new StorageChange(1, 30))
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressB)
                    .WithStorageChanges(1, new StorageChange(1, 10))
                    .WithStorageChanges(9, new StorageChange(1, 90))
                    .TestObject)
            .TestObject;

        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 1, addressIndex);
        BlockAccessListValidationIndex generatedIndex = BuildGeneratedIndex(generated, txCount: 1, addressIndex, suggestedIndex);

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.True);
    }

    [Test]
    public void ChangesEqual_sorts_large_rows_deterministically()
    {
        BlockAccessListValidationIndex.AddressIndex addressIndex = new();
        AccountChanges[] suggestedAccounts = new AccountChanges[9];
        AccountChanges[] generatedAccounts = new AccountChanges[9];

        for (int i = 0; i < suggestedAccounts.Length; i++)
        {
            Address address = TestItem.Addresses[i];
            UInt256 value = (UInt256)(i + 1);
            suggestedAccounts[i] = Build.An.AccountChanges
                .WithAddress(address)
                .WithBalanceChanges(new BalanceChange(1, value))
                .TestObject;
            generatedAccounts[suggestedAccounts.Length - i - 1] = Build.An.AccountChanges
                .WithAddress(address)
                .WithBalanceChanges(new BalanceChange(1, value))
                .TestObject;
        }

        BlockAccessList suggested = Build.A.BlockAccessList
            .WithAccountChanges(suggestedAccounts)
            .TestObject;
        BlockAccessList generated = Build.A.BlockAccessList
            .WithAccountChanges(generatedAccounts)
            .TestObject;

        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 1, addressIndex);
        BlockAccessListValidationIndex generatedIndex = BuildGeneratedIndex(generated, txCount: 1, addressIndex, suggestedIndex);

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.True);
    }

    [Test]
    public void ChangesEqual_generated_row_overflow_does_not_corrupt_later_rows()
    {
        BlockAccessListValidationIndex.AddressIndex addressIndex = new();
        BlockAccessList suggested = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(1, 1))
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressB)
                    .WithNonceChanges(new NonceChange(2, 2))
                    .TestObject)
            .TestObject;
        BlockAccessList generated = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(1, 1))
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressC)
                    .WithBalanceChanges(new BalanceChange(1, 3))
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressB)
                    .WithNonceChanges(new NonceChange(2, 2))
                    .TestObject)
            .TestObject;

        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 2, addressIndex);
        BlockAccessListValidationIndex generatedIndex = BuildGeneratedIndex(generated, txCount: 2, addressIndex, suggestedIndex);

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
        BlockAccessListValidationIndex.AddressIndex addressIndex = new();
        BlockAccessList suggested = CreateBaselineBal();
        BlockAccessList generated = CreateMismatchedBal(mismatch);

        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 3, addressIndex);
        BlockAccessListValidationIndex generatedIndex = BuildGeneratedIndex(generated, txCount: 3, addressIndex, suggestedIndex);

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, index), Is.False);
    }

    [Test]
    public void ChangesEqual_ignores_read_only_accounts()
    {
        BlockAccessListValidationIndex.AddressIndex addressIndex = new();
        BlockAccessList suggested = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithStorageReads(1, 2)
                    .TestObject)
            .TestObject;
        BlockAccessList generated = new();

        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 1, addressIndex);
        BlockAccessListValidationIndex generatedIndex = BuildGeneratedIndex(generated, txCount: 1, addressIndex, suggestedIndex);

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
        BlockAccessListValidationIndex.AddressIndex addressIndex = new();
        BlockAccessList suggested = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(3, 1))
                    .TestObject)
            .TestObject;
        BlockAccessList generated = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(3, 1))
                    .TestObject)
            .TestObject;

        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, txCount: 2, addressIndex);
        BlockAccessListValidationIndex generatedIndex = BuildGeneratedIndex(generated, txCount: 2, addressIndex, suggestedIndex);

        Assert.Multiple(() =>
        {
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 3), Is.True);
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 4), Is.False);
        });
    }

    private static BlockAccessListValidationIndex BuildGeneratedIndex(
        BlockAccessList generated,
        int txCount,
        BlockAccessListValidationIndex.AddressIndex addressIndex,
        BlockAccessListValidationIndex suggestedIndex)
    {
        BlockAccessListValidationIndex generatedIndex = new(txCount, addressIndex, suggestedIndex);
        generatedIndex.Add(generated);
        return generatedIndex;
    }

    private static BlockAccessList CreateBaselineBal(
        UInt256? balance = null,
        ulong nonce = 7,
        byte[]? code = null,
        UInt256? storageValue = null)
    {
        UInt256 actualBalance = balance ?? 11;
        UInt256 actualStorageValue = storageValue ?? 13;
        byte[] actualCode = code ?? [1, 2, 3];

        return Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressA)
                    .WithBalanceChanges(new BalanceChange(0, actualBalance))
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressB)
                    .WithNonceChanges(new NonceChange(1, nonce))
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressC)
                    .WithCodeChanges(new CodeChange(2, actualCode))
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressD)
                    .WithStorageChanges(17, new StorageChange(2, actualStorageValue))
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(TestItem.AddressE)
                    .WithBalanceChanges(new BalanceChange(4, 19))
                    .TestObject)
            .TestObject;
    }

    private static BlockAccessList CreateMismatchedBal(ValidationIndexMismatch mismatch) =>
        mismatch switch
        {
            ValidationIndexMismatch.MissingBalance => Build.A.BlockAccessList
                .WithAccountChanges(
                    Build.An.AccountChanges
                        .WithAddress(TestItem.AddressB)
                        .WithNonceChanges(new NonceChange(1, 7))
                        .TestObject,
                    Build.An.AccountChanges
                        .WithAddress(TestItem.AddressC)
                        .WithCodeChanges(new CodeChange(2, [1, 2, 3]))
                        .TestObject,
                    Build.An.AccountChanges
                        .WithAddress(TestItem.AddressD)
                        .WithStorageChanges(17, new StorageChange(2, 13))
                        .TestObject,
                    Build.An.AccountChanges
                        .WithAddress(TestItem.AddressE)
                        .WithBalanceChanges(new BalanceChange(4, 19))
                        .TestObject)
                .TestObject,
            ValidationIndexMismatch.SurplusBalance => Build.A.BlockAccessList
                .WithAccountChanges(
                    Build.An.AccountChanges
                        .WithAddress(TestItem.AddressF)
                        .WithBalanceChanges(new BalanceChange(1, 1))
                        .TestObject,
                    Build.An.AccountChanges
                        .WithAddress(TestItem.AddressA)
                        .WithBalanceChanges(new BalanceChange(0, 11))
                        .TestObject,
                    Build.An.AccountChanges
                        .WithAddress(TestItem.AddressB)
                        .WithNonceChanges(new NonceChange(1, 7))
                        .TestObject,
                    Build.An.AccountChanges
                        .WithAddress(TestItem.AddressC)
                        .WithCodeChanges(new CodeChange(2, [1, 2, 3]))
                        .TestObject,
                    Build.An.AccountChanges
                        .WithAddress(TestItem.AddressD)
                        .WithStorageChanges(17, new StorageChange(2, 13))
                        .TestObject,
                    Build.An.AccountChanges
                        .WithAddress(TestItem.AddressE)
                        .WithBalanceChanges(new BalanceChange(4, 19))
                        .TestObject)
                .TestObject,
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
