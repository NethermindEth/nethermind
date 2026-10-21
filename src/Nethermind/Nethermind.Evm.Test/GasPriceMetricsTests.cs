// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Covers the per-block gas-price gauges and base-fee seeding in <see cref="Metrics"/>.
/// </summary>
/// <remarks>
/// The gauges are static process-wide state, so these run non-parallel.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class GasPriceMetricsTests
{
    private static UInt256 Gwei(ulong gwei) => new(gwei * 1_000_000_000UL);

    private static Transaction LegacyTx(UInt256 gasPrice) => Build.A.Transaction.WithGasPrice(gasPrice).TestObject;

    [Test]
    public void PublishBlockGasPrices_WithTransactions_PublishesMinMaxAndMean()
    {
        Metrics.PublishBlockGasPrices([LegacyTx(Gwei(10)), LegacyTx(Gwei(30))], isEip1559Enabled: false, Gwei(1));

        Assert.That(Metrics.GasPriceMin, Is.EqualTo(10.0f), "lowest effective gas price of the block");
        Assert.That(Metrics.GasPriceMax, Is.EqualTo(30.0f), "highest effective gas price of the block");
        Assert.That(Metrics.GasPriceAve, Is.EqualTo(20.0f), "mean of the block's effective gas prices");
    }

    [Test]
    public void PublishBlockGasPrices_WithoutTransactions_ReportsBaseFee()
    {
        Metrics.PublishBlockGasPrices([], isEip1559Enabled: true, Gwei(2));

        const float expected = 2.0f;
        Assert.That((Metrics.GasPriceMin, Metrics.GasPriceMedian, Metrics.GasPriceAve, Metrics.GasPriceMax),
            Is.EqualTo((expected, expected, expected, expected)), "an empty block reports its base fee in every gauge");
    }

    [Test]
    public void PublishBlockGasPrices_WithoutTransactionsAndZeroBaseFee_KeepsPreviousValues()
    {
        Metrics.PublishBlockGasPrices([LegacyTx(Gwei(10))], isEip1559Enabled: false, UInt256.Zero);
        Assert.That(Metrics.GasPriceMin, Is.EqualTo(10.0f), "precondition: a priced block was published");

        Metrics.PublishBlockGasPrices([], isEip1559Enabled: false, UInt256.Zero);

        Assert.That(Metrics.GasPriceMin, Is.EqualTo(10.0f), "a zero base fee is less useful than the last block's values");
    }

    [Test]
    public void PublishBlockGasPrices_WithGasPriceAboveUlong_SkipsTheTransaction()
    {
        Metrics.PublishBlockGasPrices([LegacyTx(new UInt256(0, 0, 1, 0)), LegacyTx(Gwei(5))], isEip1559Enabled: false, Gwei(1));

        Assert.That((Metrics.GasPriceMin, Metrics.GasPriceAve, Metrics.GasPriceMax), Is.EqualTo((5.0f, 5.0f, 5.0f)),
            "a gas price of 2^128 wei is not meaningful and is skipped");
    }

    [Test]
    public void PublishBlockGasPrices_WithSystemTransaction_LeavesItOut()
    {
        Transaction system = Build.A.Transaction.WithGasPrice(UInt256.Zero).WithSenderAddress(Address.SystemUser).TestObject;

        Metrics.PublishBlockGasPrices([system, LegacyTx(Gwei(8))], isEip1559Enabled: false, Gwei(1));

        Assert.That((Metrics.GasPriceMin, Metrics.GasPriceAve, Metrics.GasPriceMax), Is.EqualTo((8.0f, 8.0f, 8.0f)),
            "a system transaction pays no gas price, so it must not pull the block's gauges to zero");
    }

    [TestCase(10ul, 30ul, 0.3f, TestName = "AscendingOrder")]
    [TestCase(30ul, 10ul, 0.5f, TestName = "DescendingOrder")]
    public void PublishBlockGasPrices_WithTransactions_EstimatesMedianInBlockOrder(ulong firstGwei, ulong secondGwei, float expectedMedian)
    {
        Metrics.PublishBlockGasPrices([LegacyTx(Gwei(firstGwei)), LegacyTx(Gwei(secondGwei))], isEip1559Enabled: false, Gwei(1));

        Assert.That(Metrics.GasPriceMedian, Is.EqualTo(expectedMedian).Within(1e-5f), "the streaming median estimate follows block order");
    }

    [TestCase(50ul, 2ul, 12.0f, TestName = "BaseFeePlusTip")]
    [TestCase(11ul, 5ul, 11.0f, TestName = "CappedByMaxFee")]
    public void PublishBlockGasPrices_WithEip1559Transaction_UsesEffectiveGasPrice(ulong maxFeeGwei, ulong tipGwei, float expected)
    {
        Transaction transaction = Build.A.Transaction.WithType(TxType.EIP1559)
            .WithMaxFeePerGas(Gwei(maxFeeGwei)).WithMaxPriorityFeePerGas(Gwei(tipGwei)).TestObject;

        Metrics.PublishBlockGasPrices([transaction], isEip1559Enabled: true, Gwei(10));

        Assert.That((Metrics.GasPriceMin, Metrics.GasPriceAve, Metrics.GasPriceMax), Is.EqualTo((expected, expected, expected)),
            "the effective gas price is the base fee plus the tip, capped by the max fee");
    }
}
