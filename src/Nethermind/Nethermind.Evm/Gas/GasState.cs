// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.Gas;

/// <summary>
/// Generic gas state container that can hold both simple and complex gas accounting.
/// </summary>
public struct GasState
{
    /// <summary>
    /// Remaining single-dimensional gas.
    /// For simple gas: this is the only field used.
    /// For multigas: this is the sum of all dimensions.
    /// </summary>
    public long RemainingGas;

    /// <summary>
    /// Policy-specific data for complex gas accounting.
    /// For simple gas: null.
    /// For multigas: holds MultiGas breakdown and other tracking data.
    /// </summary>
    public readonly object? PolicyData;

    /// <summary>
    /// Initializes a new gas state with specified remaining gas.
    /// </summary>
    /// <param name="remainingGas">The initial remaining gas</param>
    public GasState(long remainingGas)
    {
        RemainingGas = remainingGas;
        PolicyData = null;
    }

    public GasState(long remainingGas, object policyData)
    {
        RemainingGas = remainingGas;
        PolicyData = policyData;
    }

    /// <summary>
    /// Returns a string representation of the gas state for debugging.
    /// </summary>
    public readonly override string ToString()
    {
        return PolicyData is null ? $"Gas: {RemainingGas}" : $"Gas: {RemainingGas}, PolicyData: {PolicyData}";
    }
}
