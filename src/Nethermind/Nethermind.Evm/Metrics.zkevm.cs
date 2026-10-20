// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Evm;

/// <summary>
/// zkEVM guest variant of the per-block gas-price metrics: every member is a no-op.
/// </summary>
/// <remarks>
/// The std implementation aggregates in floating point per transaction, but the guest targets rv64ima
/// (no FPU) and nobody reads the gauges inside the zkVM.
/// </remarks>
public partial class Metrics
{
    public static float GasPriceMin => 0f;
    public static float GasPriceMedian => 0f;
    public static float GasPriceAve => 0f;
    public static float GasPriceMax => 0f;

    internal static void PublishBlockGasPrices(Transaction[] transactions, bool isEip1559Enabled, in UInt256 baseFee) { }
}
