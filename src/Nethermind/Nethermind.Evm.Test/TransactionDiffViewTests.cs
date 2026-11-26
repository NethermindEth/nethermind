// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>EIP-7906 diff classification, driven from a BAL slice because the delegation-designator
/// case below is not reachable through a frame transaction.</summary>
[TestFixture]
public class TransactionDiffViewTests
{
    private static readonly Address Low = new("0x0000000000000000000000000000000000000001");
    private static readonly Address Mid = new("0x0000000000000000000000000000000000000002");
    private static readonly Address High = new("0x0000000000000000000000000000000000000003");
    private static readonly byte[] Code = [0x60, 0x00];
    private static readonly byte[] Designator = [0xef, 0x01, 0x00, .. High.Bytes];

    private static TestCaseData[] CodeHashChanges() =>
    [
        new TestCaseData(Array.Empty<byte>(), Code) { TestName = "Build_CodeAppearingOnACodelessAccount_IsACodeChange" },
        new TestCaseData(new byte[] { 0x00 }, Code) { TestName = "Build_CodeReplacingExistingCode_IsACodeChange" },
        new TestCaseData(Array.Empty<byte>(), Designator) { TestName = "Build_DelegationInstalled_IsACodeChange" },
        new TestCaseData(Designator, Array.Empty<byte>()) { TestName = "Build_DelegationCleared_IsACodeChange" },
    ];

    [TestCaseSource(nameof(CodeHashChanges))]
    public void Build_EnumeratesEveryCodeHashChange(byte[] before, byte[] after)
    {
        BlockAccessListAtIndex slice = new();
        slice.AddCodeChange(Low, before, after);

        TransactionDiffView view = TransactionDiffView.Build(slice, []);

        Assert.That(view.CodeAddresses, Is.EqualTo(new[] { Low }));
    }

    [Test]
    public void Build_CodeRestoredToItsPreTxValue_IsNotACodeChange()
    {
        BlockAccessListAtIndex slice = new();
        slice.AddCodeChange(Low, Array.Empty<byte>(), Code);
        slice.AddCodeChange(Low, Code, Array.Empty<byte>());

        TransactionDiffView view = TransactionDiffView.Build(slice, []);

        Assert.That(view.CodeAddresses, Is.Empty);
    }

    [TestCase(0ul, 1ul, true, TestName = "Build_NonceOfANewAccount_IsANonceChange")]
    [TestCase(5ul, 6ul, true, TestName = "Build_IncrementedNonce_IsANonceChange")]
    [TestCase(5ul, 5ul, false, TestName = "Build_NonceBackAtItsPreTxValue_IsNotANonceChange")]
    public void Build_EnumeratesNetNonceChanges(ulong before, ulong after, bool listed)
    {
        BlockAccessListAtIndex slice = new();
        slice.AddNonceChange(Low, before, before + 1);
        if (after != before + 1) slice.AddNonceChange(Low, before + 1, after);

        TransactionDiffView view = TransactionDiffView.Build(slice, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(view.NonceAddresses, listed ? Is.EqualTo(new[] { Low }) : Is.Empty);
            Assert.That(slice.GetAccountChanges(Low)!.PreTxNonce, Is.EqualTo(before));
        }
    }

    [Test]
    public void Build_ReadOnlyAccounts_AreExcludedFromTheDiff()
    {
        BlockAccessListAtIndex slice = new();
        slice.AddAccountRead(Low);
        slice.AddBalanceChange(High, UInt256.Zero, UInt256.One);

        TransactionDiffView view = TransactionDiffView.Build(slice, []);

        Assert.That(view.BalanceAddresses, Is.EqualTo(new[] { High }));
    }

    [Test]
    public void Build_SortsSlotsByAddressThenKey()
    {
        BlockAccessListAtIndex slice = new();
        slice.AddStorageChange(High, 2, UInt256.Zero, UInt256.One);
        slice.AddStorageChange(Mid, 4, UInt256.Zero, UInt256.One);
        slice.AddStorageChange(Low, 7, UInt256.Zero, UInt256.One);
        slice.AddStorageChange(Low, 3, UInt256.Zero, UInt256.One);

        TransactionDiffView view = TransactionDiffView.Build(slice, []);

        Assert.That(view.Slots, Is.EqualTo(new[]
        {
            new TransactionDiffView.SlotRef(Low, 3),
            new TransactionDiffView.SlotRef(Low, 7),
            new TransactionDiffView.SlotRef(Mid, 4),
            new TransactionDiffView.SlotRef(High, 2),
        }));
    }

    [TestCase(false, TestName = "Build_PreTxCodeHash_UndeployedContract_IsTheEmptyCodeHash")]
    [TestCase(true, TestName = "Build_PreTxCodeHash_ExistingCode_IsItsKeccak")]
    public void GetPreTxCodeHash_HashesThePreTxCode(bool hadCode)
    {
        BlockAccessListAtIndex slice = new();
        byte[] preTxCode = hadCode ? [0x00] : [];
        slice.AddCodeChange(Low, preTxCode, Code);
        TransactionDiffView view = TransactionDiffView.Build(slice, []);
        AccountChangesAtIndex account = slice.GetAccountChanges(Low)!;

        ValueHash256 hash = view.GetPreTxCodeHash(Low, account);

        Assert.That(hash, Is.EqualTo(hadCode ? ValueKeccak.Compute(preTxCode) : ValueKeccak.OfAnEmptyString));

        // With the source cleared, only a memoized second call still returns the hash.
        account.Reset(Low);
        Assert.That(view.GetPreTxCodeHash(Low, account), Is.EqualTo(hash));
    }

    // Spec enumeration order for balance_changes is ascending by address, whatever order the slice recorded.
    [Test]
    public void Build_SortsBalanceAddressesAscending()
    {
        BlockAccessListAtIndex slice = new();
        slice.AddBalanceChange(High, UInt256.Zero, UInt256.One);
        slice.AddBalanceChange(Low, UInt256.Zero, UInt256.One);
        slice.AddBalanceChange(Mid, UInt256.Zero, UInt256.One);

        TransactionDiffView view = TransactionDiffView.Build(slice, []);

        Assert.That(view.BalanceAddresses, Is.EqualTo(new[] { Low, Mid, High }));
    }

    [Test]
    public void Build_SortsCodeAndNonceAddressesAscending()
    {
        BlockAccessListAtIndex slice = new();
        foreach (Address address in new[] { High, Low, Mid })
        {
            slice.AddCodeChange(address, Array.Empty<byte>(), Code);
            slice.AddNonceChange(address, 0, 1);
        }

        TransactionDiffView view = TransactionDiffView.Build(slice, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(view.CodeAddresses, Is.EqualTo(new[] { Low, Mid, High }));
            Assert.That(view.NonceAddresses, Is.EqualTo(new[] { Low, Mid, High }));
        }
    }
}
