// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Gas;

/// <summary>
/// Gas state containing remaining gas and an inline gas policy.
/// </summary>
/// <typeparam name="TGasPolicy">The gas policy type</typeparam>
public ref struct GasState<TGasPolicy>(long remainingGas, TGasPolicy policy = default)
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    /// <summary>
    /// Remaining gas available for execution.
    /// </summary>
    public long RemainingGas = remainingGas;

    /// <summary>
    /// Inline gas policy containing tracking data.
    /// </summary>
    public TGasPolicy Policy = policy;

    public readonly override string ToString() => $"Gas: {RemainingGas}";
}
