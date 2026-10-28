// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Mining.Test;

/// <summary>The producer's base fee filters price the next block in the slot after the parent's under EIP-8198.</summary>
[TestFixture]
public class Eip8198TxFilterTests
{
    // A full parent at 1 gwei: 1_104_166_666 at 10 s slots (5/48), 1_125_000_000 at 12 s slots (1/8).
    private const ulong FeeBetweenTheRules = 1_110_000_000;

    private static readonly IReleaseSpec Spec = Bogota.Instance;

    private static BlockHeader Parent() => Build.A.BlockHeader
        .WithNumber(10)
        .WithGasLimit(30_000_000)
        .WithGasUsed(30_000_000)
        .WithBaseFee(1_000_000_000)
        .WithTimestamp(1_000)
        .WithSlotNumber(100)
        .TestObject;

    private static Transaction Tx(ulong maxFee, ulong maxPriorityFee = 0) => Build.A.Transaction
        .WithType(TxType.EIP1559)
        .WithMaxFeePerGas(maxFee)
        .WithMaxPriorityFeePerGas(maxPriorityFee)
        .TestObject;

    [Test]
    public void Base_fee_filter_prices_the_next_slot()
    {
        Transaction tx = Tx(FeeBetweenTheRules);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new BaseFeeTxFilter(new SlotSchedule(10)).IsAllowed(tx, Parent(), Spec), Is.EqualTo(AcceptTxResult.Accepted), "10 s slots");
            Assert.That(new BaseFeeTxFilter(new SlotSchedule(12)).IsAllowed(tx, Parent(), Spec), Is.EqualTo(AcceptTxResult.FeeTooLow), "12 s slots");
            Assert.That(new BaseFeeTxFilter().IsAllowed(tx, Parent(), Spec), Is.EqualTo(AcceptTxResult.FeeTooLow), "no schedule, 12 s reference");
            Assert.That(new BaseFeeTxFilter(new SlotSchedule(10)).IsAllowed(tx, Parent(), Amsterdam.Instance), Is.EqualTo(AcceptTxResult.FeeTooLow), "before EIP-8198");
        }
    }

    [Test]
    public void Min_gas_price_filter_prices_the_next_slot()
    {
        BlocksConfig blocksConfig = new() { MinGasPrice = 1 };
        // The premium is min(tip, max fee - base fee), so it only clears the floor against the 5/48 base fee.
        Transaction tx = Tx(1_104_166_667, 1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new MinGasPriceTxFilter(blocksConfig, new SlotSchedule(10)).IsAllowed(tx, Parent(), Spec), Is.EqualTo(AcceptTxResult.Accepted));
            Assert.That(new MinGasPriceTxFilter(blocksConfig).IsAllowed(tx, Parent(), Spec), Is.EqualTo(AcceptTxResult.FeeTooLow));
        }
    }
}
