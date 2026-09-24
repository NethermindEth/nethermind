// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

    [TestCase(false, 1, TestName = "Code appearing on a codeless account is a deployment")]
    [TestCase(true, 0, TestName = "Code replacing existing code is not a deployment")]
    public void Build_EnumeratesDeploymentsByPreTxCode(bool hadCode, int expected)
    {
        BlockAccessListAtIndex slice = new();
        slice.AddCodeChange(Low, hadCode ? [0x00] : [], Code);

        TransactionDiffView view = TransactionDiffView.Build(slice, []);

        Assert.That(view.DeployedAddresses, Has.Length.EqualTo(expected));
    }

    // The spec excludes EIP-7702 delegation designators from contracts_deployed.
    [Test]
    public void Build_DelegationDesignatorOnFreshEoa_IsNotADeployment()
    {
        BlockAccessListAtIndex slice = new();
        slice.AddCodeChange(Low, [], Designator);

        TransactionDiffView view = TransactionDiffView.Build(slice, []);

        Assert.That(view.DeployedAddresses, Is.Empty);
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

    // Contiguity per address is what the run lookup behind TXDIFF 0x06/0x07 relies on.
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
        // A run past the first is what TXDIFF 0x06/0x07 misresolves if a start is ever left at zero.
        Assert.Multiple(() =>
        {
            Assert.That(SlotRun(view, Low), Is.EqualTo((0, 2)));
            Assert.That(SlotRun(view, Mid), Is.EqualTo((2, 1)));
            Assert.That(SlotRun(view, High), Is.EqualTo((3, 1)));
        });
    }

    private static (int Start, int Count) SlotRun(TransactionDiffView view, Address address)
    {
        Assert.That(view.TryGetSlotRun(address, out int start, out int count), Is.True);
        return (start, count);
    }

    // TXDIFF 0x04: the EIP pins codehash_before to the empty-code hash for an undeployed contract.
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

    // Spec enumeration order for contracts_deployed is ascending by address.
    [Test]
    public void Build_SortsDeployedAddressesAscending()
    {
        BlockAccessListAtIndex slice = new();
        slice.AddCodeChange(High, [], Code);
        slice.AddCodeChange(Low, [], Code);
        slice.AddCodeChange(Mid, [], Code);

        TransactionDiffView view = TransactionDiffView.Build(slice, []);

        Assert.That(view.DeployedAddresses, Is.EqualTo(new[] { Low, Mid, High }));
    }

    // TXDIFF 0x08/0x09: per-address event indexes stay in emission order and point back at global positions.
    [Test]
    public void Build_GroupsEventIndicesByAddressInEmissionOrder()
    {
        LogEntry[] logs = [Log(High), Log(Low), Log(High), Log(Mid), Log(High)];

        TransactionDiffView view = TransactionDiffView.Build(new BlockAccessListAtIndex(), logs);

        Assert.Multiple(() =>
        {
            Assert.That(view.AddressEventCount(High), Is.EqualTo(3));
            Assert.That(view.AddressEventCount(Low), Is.EqualTo(1));
            Assert.That(view.AddressEventCount(Mid), Is.EqualTo(1));
            Assert.That(GlobalIndices(view, High), Is.EqualTo(new[] { 0, 2, 4 }));
            Assert.That(GlobalIndices(view, Low), Is.EqualTo(new[] { 1 }));
            Assert.That(GlobalIndices(view, Mid), Is.EqualTo(new[] { 3 }));
        });
    }

    [Test]
    public void TryGetAddressEventGlobalIndex_OutOfRangeOrUnknownAddress_IsRejected()
    {
        TransactionDiffView view = TransactionDiffView.Build(new BlockAccessListAtIndex(), [Log(Low)]);

        Assert.Multiple(() =>
        {
            Assert.That(view.AddressEventCount(High), Is.Zero);
            Assert.That(view.TryGetAddressEventGlobalIndex(High, UInt256.Zero, out _), Is.False);
            Assert.That(view.TryGetAddressEventGlobalIndex(Low, UInt256.One, out _), Is.False);
            Assert.That(view.TryGetAddressEventGlobalIndex(Low, UInt256.MaxValue, out _), Is.False);
        });
    }

    private static LogEntry Log(Address address) => new(address, [], []);

    private static int[] GlobalIndices(TransactionDiffView view, Address address)
    {
        int[] indices = new int[view.AddressEventCount(address)];
        for (int i = 0; i < indices.Length; i++)
        {
            Assert.That(view.TryGetAddressEventGlobalIndex(address, (UInt256)(ulong)i, out indices[i]), Is.True);
        }
        return indices;
    }
}
