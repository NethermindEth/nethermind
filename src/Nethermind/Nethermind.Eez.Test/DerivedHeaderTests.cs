// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution.Settlement;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public class DerivedHeaderTests
{
    private const ulong BlockTime = 2;
    private static readonly EezSettlementContext Context = new(1, 6290, Address.Zero, default, BlockTime);

    [TestCaseSource(nameof(Forks))]
    public void Ensure_HeaderDerivationBuilds_Passes(IReleaseSpec spec, Hash256? beaconRoot, Withdrawal[]? withdrawals) =>
        Assert.DoesNotThrow(() => DerivedHeader.Ensure(Derived(beaconRoot, withdrawals), Parent(), spec, Context));

    [TestCaseSource(nameof(OtherForkFields))]
    public void Ensure_FieldOfAnotherFork_Throws(IReleaseSpec spec, Hash256? beaconRoot, Withdrawal[]? withdrawals) =>
        Assert.Throws<EezSettlementException>(() => DerivedHeader.Ensure(Derived(beaconRoot, withdrawals), Parent(), spec, Context));

    [Test]
    public void Ensure_ParentAtTheLastTimestamp_Saturates()
    {
        BlockHeader parent = Parent();
        parent.Timestamp = ulong.MaxValue - 1;
        Block block = Derived(Keccak.Zero, []);
        block.Header.Timestamp = ulong.MaxValue;

        Assert.DoesNotThrow(() => DerivedHeader.Ensure(block, parent, Cancun.Instance, Context), "the timestamp stops at the largest value, as derivation's does");
    }

    private static TestCaseData[] Forks() =>
    [
        new(Cancun.Instance, Keccak.Zero, Array.Empty<Withdrawal>()) { TestName = "Cancun" },
        new(Shanghai.Instance, null, Array.Empty<Withdrawal>()) { TestName = "Shanghai" },
        new(Paris.Instance, null, null) { TestName = "Paris" },
    ];

    private static TestCaseData[] OtherForkFields() =>
    [
        new(Shanghai.Instance, Keccak.Zero, Array.Empty<Withdrawal>()) { TestName = "BeaconRootBeforeCancun" },
        new(Paris.Instance, null, Array.Empty<Withdrawal>()) { TestName = "WithdrawalsBeforeShanghai" },
        new(Shanghai.Instance, null, null) { TestName = "NoWithdrawalsAfterShanghai" },
    ];

    private static BlockHeader Parent() => Build.A.BlockHeader.WithTimestamp(100).TestObject;

    private static Block Derived(Hash256? beaconRoot, Withdrawal[]? withdrawals)
    {
        BlockHeader header = Build.A.BlockHeader.WithTimestamp(100 + BlockTime).WithGasLimit((long)EezSettlementContext.DefaultGasLimit).WithMixHash(Keccak.Zero)
            .WithDifficulty(0).WithNonce(0).WithUnclesHash(Keccak.OfAnEmptySequenceRlp).WithParentBeaconBlockRoot(beaconRoot).TestObject;
        return new Block(header, [], [], withdrawals);
    }
}
