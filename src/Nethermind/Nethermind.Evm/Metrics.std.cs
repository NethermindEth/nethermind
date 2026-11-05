// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm;

public partial class Metrics
{
    [GaugeMetric]
    [Description("Minimum tx gas price in block")]
    public static float GasPriceMin { get; private set; }

    [GaugeMetric]
    [Description("Median tx gas price in block")]
    public static float GasPriceMedian { get; private set; }

    [GaugeMetric]
    [Description("Mean tx gas price in block")]
    public static float GasPriceAve { get; private set; }

    [GaugeMetric]
    [Description("Maximum tx gas price in block")]
    public static float GasPriceMax { get; private set; }

    /// <summary>Publishes the gas-price gauges of a processed block from the effective gas prices of its transactions.</summary>
    /// <remarks>
    /// Prices >= <see cref="ulong.MaxValue"/> wei/gas (~18.4 ETH) are not meaningful and are skipped,
    /// avoiding the multi-limb <see cref="UInt256"/>-to-<see cref="double"/> conversion. A block with no such
    /// transaction (empty / system-only) reports its base fee; a zero base fee (pre-EIP-1559, genesis) keeps the
    /// previous values - "0.000" is less useful than the last block's.
    /// </remarks>
    internal static void PublishBlockGasPrices(Transaction[] transactions, bool isEip1559Enabled, in UInt256 baseFee)
    {
        float min = float.MaxValue;
        float max = 0f;
        float ave = 0f;
        float median = 0f;
        int count = 0;
        foreach (Transaction transaction in transactions)
        {
            if (transaction.IsSystem()) continue;

            UInt256 effectiveGasPrice = transaction.CalculateEffectiveGasPrice(isEip1559Enabled, baseFee);
            if (!effectiveGasPrice.IsUint64) continue;

            float gasPrice = ToGwei(effectiveGasPrice.u0);
            min = MathF.Min(min, gasPrice);
            max = MathF.Max(max, gasPrice);
            ave = (ave * count + gasPrice) / ++count;
            median += ave * 0.01f * float.Sign(gasPrice - median);
        }

        if (count == 0)
        {
            if (!baseFee.IsUint64 || baseFee.IsZero) return;
            min = max = ave = median = ToGwei(baseFee.u0);
        }

        GasPriceMin = min;
        GasPriceMax = max;
        GasPriceAve = ave;
        GasPriceMedian = median;
    }

    private static float ToGwei(ulong wei) => (float)(wei / 1_000_000_000.0);
}
