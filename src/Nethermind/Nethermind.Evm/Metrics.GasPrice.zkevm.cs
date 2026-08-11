// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// zkEVM guest variant of the per-block gas-price metrics: every member is a no-op.
/// The real implementation (Metrics.GasPrice.std.cs) aggregates prices in floating
/// point on the per-transaction hot path; the guest targets rv64ima (no FPU), nobody
/// reads the gauges inside the zkVM, and skipping the three CAS loops per transaction
/// removes proving cost. Signatures mirror the std file so shared callers
/// (TransactionProcessor, BlockProcessor executors, ProcessingStats) compile unchanged.
/// </summary>
public partial class Metrics
{
    internal static long BlockTransactions => 0;
    internal static float BlockAveGasPrice => 0f;
    internal static float BlockMinGasPrice => float.MaxValue;
    internal static float BlockMaxGasPrice => 0f;
    internal static float BlockEstMedianGasPrice => 0f;

    public static (float Min, float EstMedian, float Ave, float Max)? GetBlockGasPrices() => null;

    public static float GasPriceMin => 0f;
    public static float GasPriceMedian => 0f;
    public static float GasPriceAve => 0f;
    public static float GasPriceMax => 0f;

    public static void ResetBlockStats() { }

    internal static void UpdateBlockGasPrice(in UInt256 effectiveGasPrice) { }

    internal static void SeedBlockGasPriceIfEmpty(in UInt256 baseFee) { }

    internal static void PublishBlockGasPriceGauges() { }
}
