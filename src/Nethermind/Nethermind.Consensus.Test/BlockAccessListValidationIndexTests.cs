// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Consensus.Test;

public class BlockAccessListValidationIndexTests
{
    private static readonly BlockHeader Header = Build.A.BlockHeader.TestObject;

    private BlockAccessListValidationIndex.AddressIndex _addressIndex = null!;

    [SetUp]
    public void SetUp() => _addressIndex = new();

    [Test]
    public void ChangesEqual_matches_exact_per_index_deltas()
    {
        ReadOnlyBlockAccessList suggested = CreateBaselineBal();
        ReadOnlyBlockAccessList generated = CreateBaselineBal();

        BlockAccessListValidationIndex generatedIndex = BuildPair(suggested, generated, txCount: 3, out BlockAccessListValidationIndex suggestedIndex);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 0), Is.True);
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.True);
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 2), Is.True);
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 3), Is.True);
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 4), Is.True);
        }
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
    public void ChangesEqual_sorts_generated_scalar_row_added_out_of_account_order()
    {
        ReadOnlyBlockAccessList suggested = Bal(
            Account(TestItem.AddressA, balance: new BalanceChange(1, 1)),
            Account(TestItem.AddressB, balance: new BalanceChange(1, 2)),
            Account(TestItem.AddressC, balance: new BalanceChange(1, 3)));
        using BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, Header, txCount: 1, _addressIndex);
        using BlockAccessListValidationIndex generatedIndex = new(1, _addressIndex, suggestedIndex, suggested.TotalStorageReads, suggested.TotalStorageChangeEvents);
        BlockAccessListAtIndex slice = new() { Index = 1 };
        foreach (int i in (ReadOnlySpan<int>)[0, 2, 1])
        {
            ReadOnlyAccountChanges account = suggested.AccountChanges.AsSpan()[i];
            UInt256 value = account.BalanceChanges[0].Value;
            slice.AddBalanceChange(account.Address, before: value + UInt256.One, after: value);
        }
        generatedIndex.Add(slice);

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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.False);
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 2), Is.True);
        }
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
    public void Build_rejects_change_past_post_execution_index([Values] ChangeKind kind, [Values] bool pastPostExecution)
    {
        const int txCount = 1;
        uint index = txCount + (pastPostExecution ? 2u : 1u);
        ReadOnlyBlockAccessList suggested = Bal(kind switch
        {
            ChangeKind.Balance => Account(TestItem.AddressA, balance: new BalanceChange(index, 1)),
            ChangeKind.Nonce => Account(TestItem.AddressA, nonce: new NonceChange(index, 1)),
            ChangeKind.Code => Account(TestItem.AddressA, code: new CodeChange(index, [1])),
            _ => Account(TestItem.AddressA, storage: (1, new StorageChange(index, 1))),
        });

        Action build = () => BlockAccessListValidationIndex.Build(suggested, Header, txCount, _addressIndex).Dispose();

        Assert.That(build, pastPostExecution
            ? Throws.InstanceOf<InvalidBlockException>().With.Message.Contains("exceeds allowed range")
            : Throws.Nothing);
    }

    // EIP-8298: a code change matches only in the same form, so adopted code recorded as bytecode is rejected.
    [Test]
    public void ChangesEqual_compares_code_change_form([Values] bool suggestedAdopted, [Values] bool generatedAdopted)
    {
        byte[] code = [1, 2, 3];
        ReadOnlyBlockAccessList suggested = Bal(Account(TestItem.AddressC, code: CodeChangeOf(suggestedAdopted)));
        ReadOnlyBlockAccessList generated = Bal(Account(TestItem.AddressC, code: CodeChangeOf(generatedAdopted)));

        BlockAccessListValidationIndex generatedIndex = BuildPair(suggested, generated, txCount: 3, out BlockAccessListValidationIndex suggestedIndex);

        Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 2), Is.EqualTo(suggestedAdopted == generatedAdopted));

        CodeChange CodeChangeOf(bool adopted) => adopted ? CodeChange.Adopted(2, ValueKeccak.Compute(code), code) : new CodeChange(2, code);
    }

    [Test]
    public void ChangesEqual_does_not_detect_surplus_read_only_account_in_generated()
    {
        // Read-only accounts produce no lane rows, so the column-index can't see a surplus
        // one — ChangesEqual returns true. The manager's _hasGeneratedRequiredReadAccountMismatch
        // flag (set in RegisterGeneratedSlice) catches it instead.
        ReadOnlyBlockAccessList suggested = Bal(
            Account(TestItem.AddressA, balance: new BalanceChange(1, 1)));

        BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, Header, txCount: 1, _addressIndex);
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.True);
            Assert.That(suggestedIndex.HasAccount(TestItem.AddressA), Is.True);
            Assert.That(suggestedIndex.HasAccount(TestItem.AddressB), Is.False);
        }
    }

    [Test]
    public void ChangesEqual_supports_post_execution_index()
    {
        ReadOnlyBlockAccessList suggested = Bal(Account(TestItem.AddressA, balance: new BalanceChange(3, 1)));
        ReadOnlyBlockAccessList generated = Bal(Account(TestItem.AddressA, balance: new BalanceChange(3, 1)));

        BlockAccessListValidationIndex generatedIndex = BuildPair(suggested, generated, txCount: 2, out BlockAccessListValidationIndex suggestedIndex);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 3), Is.True);
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 4), Is.False);
        }
    }

    [Test]
    public void Disabling_read_tracking_preserves_write_validation([Values] bool trackStorageReads, [Values] bool incorrectWrite)
    {
        ReadOnlyBlockAccessList suggested = Bal(Build.An.AccountChanges.WithAddress(TestItem.AddressA)
            .WithStorageReads(1)
            .WithStorageChanges(3, new StorageChange(1, 7u))
            .WithStorageChanges(2, new StorageChange(1, 5u)).TestObject);
        using BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, Header, 1, _addressIndex);
        using BlockAccessListValidationIndex generatedIndex = new(1, _addressIndex, suggestedIndex,
            suggested.TotalStorageReads, suggested.TotalStorageChangeEvents, trackStorageReads);
        BlockAccessListAtIndex slice = new() { Index = 1 };
        slice.AddStorageChange(TestItem.AddressA, 3, before: 0, after: incorrectWrite ? 8u : 7u);
        slice.AddStorageChange(TestItem.AddressA, 2, before: 0, after: 5);
        generatedIndex.Add(slice);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(generatedIndex.ChangesEqual(suggestedIndex, 1), Is.EqualTo(!incorrectWrite));
            Assert.That(generatedIndex.FindStructuralMismatch(suggested, out _, out _), Is.EqualTo(trackStorageReads
                ? BlockAccessListValidationIndex.StructuralMismatchKind.StorageReadsCountMismatch
                : BlockAccessListValidationIndex.StructuralMismatchKind.None));
        }
    }

    private BlockAccessListValidationIndex BuildPair(
        ReadOnlyBlockAccessList suggested,
        ReadOnlyBlockAccessList generated,
        int txCount,
        out BlockAccessListValidationIndex suggestedIndex)
    {
        suggestedIndex = BlockAccessListValidationIndex.Build(suggested, Header, txCount, _addressIndex);
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
                GetSlice(slicesByIndex, nc.Index).AddNonceChange(acc.Address, 0, nc.Value);
            foreach (CodeChange cc in acc.CodeChanges)
            {
                if (cc.IsAdopted) GetSlice(slicesByIndex, cc.Index).AddAdoptedCodeChange(acc.Address, before: Array.Empty<byte>(), after: cc.Code, cc.CodeHash);
                else GetSlice(slicesByIndex, cc.Index).AddCodeChange(acc.Address, before: Array.Empty<byte>(), after: cc.Code);
            }
            foreach (ReadOnlySlotChanges slot in acc.StorageChanges)
            {
                foreach (StorageChange ch in slot.Changes)
                {
                    UInt256 value = ch.Value;
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
    }

    private const int ConcurrentTxCount = 256;

    /// <summary>
    /// Suggested BAL for <see cref="ConcurrentTxCount"/> self-transfers: tx i (row i + 1) changes sender i's balance and
    /// nonce, and every tx pays the coinbase.
    /// </summary>
    private static (ReadOnlyBlockAccessList Bal, BlockAccessListAtIndex[] Slices) SelfTransferBlock()
    {
        Address coinbase = TestItem.AddressA;
        List<ReadOnlyAccountChanges> accounts = [];
        BalanceChange[] coinbaseChanges = new BalanceChange[ConcurrentTxCount];
        BlockAccessListAtIndex[] slices = new BlockAccessListAtIndex[ConcurrentTxCount];
        for (int i = 0; i < ConcurrentTxCount; i++)
        {
            uint row = (uint)(i + 1);
            Address sender = new(Keccak.Compute(BitConverter.GetBytes(i)).Bytes[..20]);
            accounts.Add(Account(sender, balance: new BalanceChange(row, (UInt256)(1000 - i)), nonce: new NonceChange(row, 1)));
            coinbaseChanges[i] = new BalanceChange(row, (UInt256)(i + 1));

            BlockAccessListAtIndex slice = new() { Index = row };
            slice.AddBalanceChange(sender, before: 1001, after: (UInt256)(1000 - i));
            slice.AddNonceChange(sender, before: 0, after: 1);
            slice.AddBalanceChange(coinbase, before: (UInt256)i, after: (UInt256)(i + 1));
            slices[i] = slice;
        }

        accounts.Add(Build.An.AccountChanges.WithAddress(coinbase).WithBalanceChanges(coinbaseChanges).TestObject);
        accounts.Sort(static (a, b) => a.Address.CompareTo(b.Address));
        return (Bal([.. accounts]), slices);
    }

    [Test]
    public void TryAddConcurrent_from_many_threads_matches_serial_add()
    {
        (ReadOnlyBlockAccessList suggested, BlockAccessListAtIndex[] slices) = SelfTransferBlock();
        using BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, Header, ConcurrentTxCount, _addressIndex);
        using BlockAccessListValidationIndex serial = new(ConcurrentTxCount, _addressIndex, suggestedIndex, 0, 0);
        using BlockAccessListValidationIndex concurrent = new(ConcurrentTxCount, _addressIndex, suggestedIndex, 0, 0);

        foreach (BlockAccessListAtIndex slice in slices) serial.Add(slice);

        int[] order = new int[ConcurrentTxCount];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        new Random(42).Shuffle(order);
        bool[] accepted = new bool[ConcurrentTxCount];
        System.Threading.Tasks.Parallel.For(0, order.Length, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = 16 },
            k => accepted[order[k]] = concurrent.TryAddConcurrent(slices[order[k]]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.All.True);
            for (uint row = 1; row <= ConcurrentTxCount; row++)
            {
                Assert.That(serial.ChangesEqual(suggestedIndex, row), Is.True, $"serial row {row}");
                Assert.That(concurrent.ChangesEqual(suggestedIndex, row), Is.True, $"concurrent row {row}");
            }
            Assert.That(concurrent.MarkedAccountCount, Is.EqualTo(serial.MarkedAccountCount));
            Assert.That(concurrent.EnumerateMarkedOrdinals(), Is.EqualTo(serial.EnumerateMarkedOrdinals()));
            Assert.That(concurrent.FindStructuralMismatch(suggested, out _, out _), Is.EqualTo(BlockAccessListValidationIndex.StructuralMismatchKind.None));
        }
    }

    [Test]
    public void Account_bitmaps_do_not_inherit_bits_from_a_previous_larger_index()
    {
        (ReadOnlyBlockAccessList large, BlockAccessListAtIndex[] slices) = SelfTransferBlock();
        BlockAccessListValidationIndex.AddressIndex largeAddresses = new();
        using (BlockAccessListValidationIndex largeSuggested = BlockAccessListValidationIndex.Build(large, Header, ConcurrentTxCount, largeAddresses))
        using (BlockAccessListValidationIndex largeGenerated = new(ConcurrentTxCount, largeAddresses, largeSuggested, 0, 0))
        {
            foreach (BlockAccessListAtIndex slice in slices) largeGenerated.Add(slice);
        }

        ReadOnlyBlockAccessList small = Bal(Account(TestItem.AddressA, balance: new BalanceChange(1, 5)));
        using BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(small, Header, txCount: 1, _addressIndex);
        using BlockAccessListValidationIndex generated = new(1, _addressIndex, suggestedIndex, 0, 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(suggestedIndex.MarkedAccountCount, Is.EqualTo(1));
            Assert.That(suggestedIndex.EnumerateMarkedOrdinals(), Is.EqualTo(new[] { 0 }));
            Assert.That(generated.MarkedAccountCount, Is.Zero);
        }
    }

    [Test]
    public void TryAddConcurrent_refuses_undeclared_address_without_writing_and_serial_add_takes_it()
    {
        ReadOnlyBlockAccessList suggested = Bal(Account(TestItem.AddressA, balance: new BalanceChange(1, 5)));
        using BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, Header, txCount: 1, _addressIndex);
        using BlockAccessListValidationIndex generated = new(1, _addressIndex, suggestedIndex, 0, 0);
        BlockAccessListAtIndex slice = new() { Index = 1 };
        slice.AddBalanceChange(TestItem.AddressA, before: 4, after: 5);
        slice.AddBalanceChange(TestItem.AddressF, before: 1, after: 2);

        bool accepted = generated.TryAddConcurrent(slice);
        int markedAfterRefusal = generated.MarkedAccountCount;
        generated.Add(slice);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.False);
            Assert.That(markedAfterRefusal, Is.Zero);
            Assert.That(generated.HasAccount(TestItem.AddressF), Is.True);
            Assert.That(suggestedIndex.HasAccount(TestItem.AddressF), Is.False);
            Assert.That(generated.MarkedAccountCount, Is.EqualTo(2));
            Assert.That(generated.EnumerateMarkedOrdinals(), Is.Ordered.Ascending);
        }
    }

    [Test]
    public void TryAddConcurrent_refuses_storage_slices()
    {
        ReadOnlyBlockAccessList suggested = Bal(Account(TestItem.AddressA, storage: (1, new StorageChange(1, 7))));
        using BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, Header, txCount: 1, _addressIndex);
        using BlockAccessListValidationIndex generated = new(1, _addressIndex, suggestedIndex, 0, 1);
        BlockAccessListAtIndex slice = new() { Index = 1 };
        slice.AddStorageChange(TestItem.AddressA, 1, before: 0, after: 7);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated.TryAddConcurrent(slice), Is.False);
            Assert.That(generated.MarkedAccountCount, Is.Zero);
        }
    }

    [Test]
    public void TryAddConcurrent_reports_lowest_overflow_index_whatever_the_arrival_order()
    {
        // Rows 1 and 2 each declare one balance change; each generated slice brings two, overflowing its row.
        ReadOnlyBlockAccessList suggested = Bal(
            Build.An.AccountChanges.WithAddress(TestItem.AddressA).WithBalanceChanges(new BalanceChange(1, 1), new BalanceChange(2, 2)).TestObject,
            Account(TestItem.AddressB, balance: new BalanceChange(3, 3)));
        using BlockAccessListValidationIndex suggestedIndex = BlockAccessListValidationIndex.Build(suggested, Header, txCount: 3, _addressIndex);
        using BlockAccessListValidationIndex generated = new(3, _addressIndex, suggestedIndex, 0, 0);
        static BlockAccessListAtIndex Overflowing(uint row)
        {
            BlockAccessListAtIndex slice = new() { Index = row };
            slice.AddBalanceChange(TestItem.AddressA, before: 0, after: row);
            slice.AddBalanceChange(TestItem.AddressB, before: 0, after: 9);
            return slice;
        }

        generated.TryAddConcurrent(Overflowing(2));
        generated.TryAddConcurrent(Overflowing(1));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(generated.TryGetGeneratedOverflow(out _, out uint index), Is.True);
            Assert.That(index, Is.EqualTo(1u));
            Assert.That(generated.ChangesEqual(suggestedIndex, 1), Is.False);
            Assert.That(generated.ChangesEqual(suggestedIndex, 2), Is.False);
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

    public enum ChangeKind
    {
        Balance,
        Nonce,
        Code,
        Storage
    }
}
