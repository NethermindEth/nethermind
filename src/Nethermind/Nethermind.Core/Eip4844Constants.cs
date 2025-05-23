// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>
/// Represents the <see href="https://eips.ethereum.org/EIPS/eip-4844#parameters">EIP-4844</see> parameters.
/// </summary>
public class Eip4844Constants
{
    public const int MinBlobsPerTransaction = 1;

    /// <summary>
    /// Gets the <c>GAS_PER_BLOB</c> parameter.
    /// </summary>
    /// <remarks>Defaults to 131072.</remarks>
    public const ulong GasPerBlob = 131072;

    /// <summary>
    /// Gets the <c>TARGET_BLOB_GAS_PER_BLOCK / GAS_PER_BLOB</c> parameter.
    /// </summary>
    /// <remarks>Defaults to 3.</remarks>
    public const ulong DefaultTargetBlobCount = 3;

    /// <summary>
    /// Gets the <c>MAX_BLOB_GAS_PER_BLOCK / GAS_PER_BLOB</c> parameter.
    /// </summary>
    /// <remarks>Defaults to 6.</remarks>
    public const ulong DefaultMaxBlobCount = 6;

    /// <summary>
    /// Gets the <c>BLOB_GASPRICE_UPDATE_FRACTION</c> parameter.
    /// </summary>
    /// <remarks>Defaults to 3338477.</remarks>
    public static UInt256 DefaultBlobGasPriceUpdateFraction { get; private set; } = 3338477;

    /// <summary>
    /// Gets the <c>MIN_BLOB_GASPRICE</c> parameter, in wei.
    /// </summary>
    /// <remarks>Defaults to 1.</remarks>
    public static UInt256 MinBlobGasPrice { get; private set; } = 1;


    // The parameter mutators are kept separate deliberately to ensure no accidental value changes.
    public static void OverrideIfAny(UInt256? minBlobGasPrice = null)
    {
        if (minBlobGasPrice.HasValue)
            MinBlobGasPrice = minBlobGasPrice.Value;
    }
}
