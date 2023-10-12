// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>
/// Represents the <see href="https://eips.ethereum.org/EIPS/eip-4844#parameters">EIP-4844</see> parameters.
/// </summary>
public class Eip4844Constants
{
    /// <summary>
    /// Gets the <c>GAS_PER_BLOB</c> parameter.
    /// </summary>
    /// <remarks>Defaults to 2e17.</remarks>
    public const ulong GasPerBlob = 1 << 17;

    public const int MinBlobsPerTransaction = 1;

    /// <summary>
    /// Gets the <c>BLOB_GASPRICE_UPDATE_FRACTION</c> parameter.
    /// </summary>
    /// <remarks>Defaults to 3338477.</remarks>
    public static UInt256 BlobGasPriceUpdateFraction { get; set; } = 3338477;

    /// <summary>
    /// Gets the <c>MAX_BLOB_GAS_PER_BLOCK</c> parameter.
    /// </summary>
    /// <remarks>Defaults to 786432.</remarks>
    public static ulong MaxBlobGasPerBlock { get; set; } = GasPerBlob * 6;

    public static ulong MaxBlobGasPerTransaction => MaxBlobGasPerBlock;

    /// <summary>
    /// Gets the <c>MIN_BLOB_GASPRICE</c> parameter, in wei.
    /// </summary>
    /// <remarks>Defaults to 1.</remarks>
    public static UInt256 MinBlobGasPrice { get; set; } = 1;

    /// <summary>
    /// Gets the <c>TARGET_BLOB_GAS_PER_BLOCK</c> parameter.
    /// </summary>
    /// <remarks>Defaults to 393216.</remarks>
    public static ulong TargetBlobGasPerBlock { get; set; } = GasPerBlob * 3;
}
