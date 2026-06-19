// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Covers the parallel-safe block gas-price aggregation and base-fee seeding in <see cref="Metrics"/>.
/// </summary>
/// <remarks>
/// The aggregates are static process-wide state, so these run non-parallel and reset in setup.
/// </remarks>
[TestFixture]
[NonParallelizable]
public class GasPriceMetricsTests
{
    private static UInt256 Gwei(ulong gwei) => new(gwei * 1_000_000_000UL);

    [SetUp]
    public void SetUp() => Metrics.ResetBlockStats();

    [Test]
    public void Concurrent_updates_do_not_lose_transactions_and_track_min_max()
    {
        const int count = 10_000;

        // Each "tx" contributes a gas price of (i % 100 + 1) gwei across many threads at once.
        Parallel.For(0, count, i => Metrics.UpdateBlockGasPrice(Gwei((ulong)(i % 100) + 1)));

        (float Min, float EstMedian, float Ave, float Max)? prices = Metrics.GetBlockGasPrices();

        Assert.That(Metrics.BlockTransactions, Is.EqualTo(count), "no updates lost under contention");
        Assert.That(prices, Is.Not.Null);
        Assert.That(prices!.Value.Min, Is.EqualTo(1.0f), "exact min regardless of interleaving");
        Assert.That(prices.Value.Max, Is.EqualTo(100.0f), "exact max regardless of interleaving");
        Assert.That(prices.Value.Ave, Is.InRange(1.0f, 100.0f));
    }

    [Test]
    public void Seed_uses_base_fee_when_no_transaction_contributed()
    {
        Metrics.SeedBlockGasPriceIfEmpty(Gwei(2));

        (float Min, float EstMedian, float Ave, float Max)? prices = Metrics.GetBlockGasPrices();
        Assert.That(prices, Is.Not.Null);
        Assert.That(prices!.Value.Min, Is.EqualTo(2.0f));
        Assert.That(prices.Value.Max, Is.EqualTo(2.0f));
        Assert.That(prices.Value.Ave, Is.EqualTo(2.0f));
        Assert.That(prices.Value.EstMedian, Is.EqualTo(2.0f));
    }

    [Test]
    public void Seed_does_not_render_zero_base_fee()
    {
        Metrics.SeedBlockGasPriceIfEmpty(UInt256.Zero);

        Assert.That(Metrics.GetBlockGasPrices(), Is.Null, "zero base fee stays blank rather than showing 0.000");
    }

    [Test]
    public void Seed_does_not_override_a_contributing_transaction()
    {
        Metrics.UpdateBlockGasPrice(Gwei(50));
        Metrics.SeedBlockGasPriceIfEmpty(Gwei(2));

        (float Min, float EstMedian, float Ave, float Max)? prices = Metrics.GetBlockGasPrices();
        Assert.That(prices, Is.Not.Null);
        Assert.That(prices!.Value.Min, Is.EqualTo(50.0f), "real tx price wins over base-fee seed");
    }

    [Test]
    public void Gas_price_at_or_above_ulong_max_is_skipped()
    {
        // > ulong.MaxValue wei/gas (~18.4 ETH) is not a meaningful gas price.
        Metrics.UpdateBlockGasPrice(new UInt256(0, 0, 1, 0));

        Assert.That(Metrics.BlockTransactions, Is.EqualTo(0));
        Assert.That(Metrics.GetBlockGasPrices(), Is.Null);
    }

    [Test]
    public void Gauges_publish_final_aggregates_not_per_tx_worker_value()
    {
        Metrics.UpdateBlockGasPrice(Gwei(10));
        Metrics.UpdateBlockGasPrice(Gwei(30));

        // Per-tx updates do not touch the gauges; only the explicit publish does (once, after workers join).
        Metrics.PublishBlockGasPriceGauges();

        Assert.That(Metrics.GasPriceMin, Is.EqualTo(10.0f));
        Assert.That(Metrics.GasPriceMax, Is.EqualTo(30.0f));
        Assert.That(Metrics.GasPriceAve, Is.EqualTo(20.0f));
    }
}
