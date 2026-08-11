// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// EIP-7906 <c>contracts_deployed</c> classification and diff filtering, driven straight from a BAL
/// slice: the delegation-designator case is not reachable through a frame transaction end to end.
/// </summary>
[TestFixture]
public class TransactionDiffViewTests
{
    private static readonly Address Low = new("0x0000000000000000000000000000000000000001");
    private static readonly Address High = new("0x0000000000000000000000000000000000000002");
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

    // The spec excludes a code hash that is an EIP-7702 delegation designator, so authorising a fresh
    // EOA must not surface as a deployed contract.
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

    // Slots are ascending by (address, key) and each address's slots are contiguous, which is what the
    // per-address run lookup behind TXDIFF 0x06/0x07 relies on.
    [Test]
    public void Build_SortsSlotsByAddressThenKey()
    {
        BlockAccessListAtIndex slice = new();
        slice.AddStorageChange(High, 2, UInt256.Zero, UInt256.One);
        slice.AddStorageChange(Low, 7, UInt256.Zero, UInt256.One);
        slice.AddStorageChange(Low, 3, UInt256.Zero, UInt256.One);

        TransactionDiffView view = TransactionDiffView.Build(slice, []);

        Assert.That(view.Slots, Is.EqualTo(new[]
        {
            new TransactionDiffView.SlotRef(Low, 3),
            new TransactionDiffView.SlotRef(Low, 7),
            new TransactionDiffView.SlotRef(High, 2),
        }));
        Assert.That(view.TryGetSlotRun(Low, out int start, out int count), Is.True);
        Assert.That((start, count), Is.EqualTo((0, 2)));
    }
}
