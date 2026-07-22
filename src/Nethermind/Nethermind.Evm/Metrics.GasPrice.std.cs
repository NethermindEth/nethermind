// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Attributes;
using Nethermind.Int256;

namespace Nethermind.Evm;

public partial class Metrics
{
    // Lock-free per-tx block gas-price aggregates: each packs two interdependent values into one long
    // CAS'd atomically - (min, max) and (count, running average). Not cache-line padded: they are
    // true-shared (every worker CASes all three per tx), so padding cannot reduce the inherent contention.
    private static long _minMaxGasPriceBits = PackFloats(float.MaxValue, 0f);
    private static long _countAveGasPriceBits;
    // Order-dependent streaming estimate (already non-deterministic under parallelism); its own CAS.
    private static float _blockEstMedianGasPrice;

    internal static long BlockTransactions => LoInt(Volatile.Read(ref _countAveGasPriceBits));
    internal static float BlockAveGasPrice => HiFloat(Volatile.Read(ref _countAveGasPriceBits));
    internal static float BlockMinGasPrice => LoFloat(Volatile.Read(ref _minMaxGasPriceBits));
    internal static float BlockMaxGasPrice => HiFloat(Volatile.Read(ref _minMaxGasPriceBits));
    internal static float BlockEstMedianGasPrice => Volatile.Read(ref _blockEstMedianGasPrice);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PackFloats(float lo, float hi)
        => (uint)BitConverter.SingleToInt32Bits(lo) | ((long)(uint)BitConverter.SingleToInt32Bits(hi) << 32);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long PackCountAve(int count, float ave)
        => (uint)count | ((long)(uint)BitConverter.SingleToInt32Bits(ave) << 32);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LoFloat(long bits) => BitConverter.Int32BitsToSingle((int)bits);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float HiFloat(long bits) => BitConverter.Int32BitsToSingle((int)(bits >> 32));
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LoInt(long bits) => (int)bits;

    /// <summary>
    /// Gets block gas price data for external access. Returns (min, estMedian, ave, max).
    /// Returns null if no gas data available (min is float.MaxValue).
    /// </summary>
    public static (float Min, float EstMedian, float Ave, float Max)? GetBlockGasPrices()
    {
        float min = BlockMinGasPrice;
        return min == float.MaxValue ? null : (min, BlockEstMedianGasPrice, BlockAveGasPrice, BlockMaxGasPrice);
    }

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

    public static void ResetBlockStats()
    {
        Volatile.Write(ref _minMaxGasPriceBits, PackFloats(float.MaxValue, 0f));
        Volatile.Write(ref _countAveGasPriceBits, 0L);
        Volatile.Write(ref _blockEstMedianGasPrice, 0f);
    }

    /// <summary>Folds a transaction's effective gas price into the per-block aggregates (lock-free).</summary>
    /// <remarks>
    /// Prices >= <see cref="ulong.MaxValue"/> wei/gas (~18.4 ETH) are not meaningful and are skipped,
    /// avoiding the multi-limb <see cref="UInt256"/>-to-<see cref="double"/> conversion.
    /// </remarks>
    internal static void UpdateBlockGasPrice(in UInt256 effectiveGasPrice)
    {
        if (!effectiveGasPrice.IsUint64) return;

        float gasPrice = (float)(effectiveGasPrice.u0 / 1_000_000_000.0);

        long mm = Volatile.Read(ref _minMaxGasPriceBits);
        while (true)
        {
            long updated = PackFloats(MathF.Min(LoFloat(mm), gasPrice), MathF.Max(HiFloat(mm), gasPrice));
            if (updated == mm) break;
            long prev = Interlocked.CompareExchange(ref _minMaxGasPriceBits, updated, mm);
            if (prev == mm) break;
            mm = prev;
        }

        float newAve;
        long ca = Volatile.Read(ref _countAveGasPriceBits);
        while (true)
        {
            int count = LoInt(ca);
            newAve = (HiFloat(ca) * count + gasPrice) / (count + 1);
            long prev = Interlocked.CompareExchange(ref _countAveGasPriceBits, PackCountAve(count + 1, newAve), ca);
            if (prev == ca) break;
            ca = prev;
        }

        float median = Volatile.Read(ref _blockEstMedianGasPrice);
        while (true)
        {
            float updated = median + newAve * 0.01f * float.Sign(gasPrice - median);
            if (updated == median) break;
            float prev = Interlocked.CompareExchange(ref _blockEstMedianGasPrice, updated, median);
            if (prev == median) break;
            median = prev;
        }
        // Gauges published once by PublishBlockGasPriceGauges after workers join, not here (a slow
        // worker could otherwise leave a stale value).
    }

    /// <summary>
    /// Seeds the gas-price aggregates with the block base fee when no transaction contributed (empty /
    /// system-only block). Skips zero base fee (pre-EIP-1559, genesis) - "0.000" is less useful than blank.
    /// </summary>
    internal static void SeedBlockGasPriceIfEmpty(in UInt256 baseFee)
    {
        if (!baseFee.IsUint64 || baseFee.IsZero) return;

        float gasPrice = (float)(baseFee.u0 / 1_000_000_000.0);

        long empty = PackFloats(float.MaxValue, 0f);
        if (Interlocked.CompareExchange(ref _minMaxGasPriceBits, PackFloats(gasPrice, gasPrice), empty) != empty)
            return; // a transaction already contributed

        // Only ever called after all tx workers have joined, so no concurrent UpdateBlockGasPrice can
        // observe the gap between the CAS above and these non-atomic seed writes.
        Volatile.Write(ref _countAveGasPriceBits, PackCountAve(0, gasPrice));
        Volatile.Write(ref _blockEstMedianGasPrice, gasPrice);
    }

    /// <summary>
    /// Publishes the latest-block gas-price gauges from the final aggregates. Call once after all
    /// (possibly parallel) transactions are processed, so a slow worker cannot leave a stale value.
    /// </summary>
    internal static void PublishBlockGasPriceGauges()
    {
        float min = BlockMinGasPrice;
        if (min == float.MaxValue) return; // no data this block; keep previous gauge values

        GasPriceMin = min;
        GasPriceMax = BlockMaxGasPrice;
        GasPriceAve = BlockAveGasPrice;
        GasPriceMedian = BlockEstMedianGasPrice;
    }
}
