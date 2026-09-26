// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Tracing;

[Parallelizable(ParallelScope.All)]
public class EstimateGasTracerTests
{
    // #13197. The EIP-150 64/63 rule is applied once per nesting level to an already-scaled figure, so the advisory
    // MaxGasNeeded compounds with call depth and saturates a few hundred frames down. Everything on the way to
    // GasEstimator.CheckFunds saturates for that reason - this pins the last step, where an unchecked add would wrap
    // a saturated figure back to a small number and report it as a plausible-looking estimate.
    [Test]
    public void CalculateAdditionalGasRequired_saturates_instead_of_wrapping_past_a_saturated_child_total()
    {
        const ulong rootGas = 1UL;
        const ulong claimableRefund = 600_000UL;

        EstimateGasTracer tracer = new();

        // Root frame. IntrinsicGasAt is taken from this call, so keep it small: the wrap only shows when the
        // additional gas is nearer ulong.MaxValue than the refund that is added to it.
        tracer.ReportAction(rootGas, UInt256.Zero, TestItem.AddressA, TestItem.AddressB, default, ExecutionType.TRANSACTION);

        // One child whose own figure is already saturated, which is what a deep call chain produces.
        tracer.ReportAction(0UL, UInt256.Zero, TestItem.AddressB, TestItem.AddressB, default, ExecutionType.CALL);
        tracer.ReportExtraGasPressure(ulong.MaxValue);
        tracer.ReportActionEnd(0UL, default);

        // MaxRefundQuotientEIP3529 is 5, so the claimable refund is capped at a fifth of the non-intrinsic gas.
        tracer.ReportRefund((long)claimableRefund);
        Transaction tx = Build.A.Transaction.WithGasLimit(claimableRefund * 5UL + rootGas).TestObject;

        Assert.That(tracer.CalculateAdditionalGasRequired(tx, Cancun.Instance), Is.EqualTo(ulong.MaxValue),
            "an unchecked add wraps ulong.MaxValue - 1 + 600000 round to a small, plausible-looking estimate");
    }
}
