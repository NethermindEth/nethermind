// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Constants for L1CALL precompile operations.
/// </summary>
public static class L1CallConstants
{
    /// <summary>
    /// Number of bytes for the gas parameter
    /// </summary>
    public const int GasBytes = 8;

    /// <summary>
    /// Number of bytes for the L1 contract address
    /// </summary>
    public const int AddressBytes = 20;

    /// <summary>
    /// Number of bytes for the value parameter
    /// </summary>
    public const int ValueBytes = 32;

    /// <summary>
    /// Number of bytes for the callDataSize parameter
    /// </summary>
    public const int CallDataSizeBytes = 8;

    /// <summary>
    /// Number of bytes for the feePerGas parameter
    /// </summary>
    public const int FeePerGasBytes = 32;

    /// <summary>
    /// Minimum input length for L1CALL precompile calls (fixed fields only, without call data)
    /// </summary>
    public const int MinInputLength = GasBytes + AddressBytes + ValueBytes + CallDataSizeBytes + FeePerGasBytes;

    /// <summary>
    /// Fixed gas cost for L1CALL precompile calls
    /// </summary>
    public const long FixedGasCost = 3000L;

    /// <summary>
    /// Per-call gas cost for L1CALL precompile calls
    /// </summary>
    public const long PerCallGasCost = 3000L;
}
