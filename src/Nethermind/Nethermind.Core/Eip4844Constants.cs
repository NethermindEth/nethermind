// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>
/// Represents the <see href="https://eips.ethereum.org/EIPS/eip-4844#parameters">EIP-4844</see> parameters.
/// </summary>
public class Eip4844Constants
{
    private static UInt256? OveriddenMinBlobGasPrice;
    private static readonly UInt256 Eip7762MinBlobGasPrice = new(1 << 25); // 2^25

    public const int MinBlobsPerTransaction = 1;

    /// <summary>
    /// Gets the <c>GAS_PER_BLOB</c> parameter.
    /// </summary>
    /// <remarks>Defaults to 131072.</remarks>
    public const ulong GasPerBlob = 131072;

    /// <summary>
    /// Gets the <c>BLOB_GASPRICE_UPDATE_FRACTION</c> parameter.
    /// </summary>
    /// <remarks>Defaults to 3338477.</remarks>
    public static UInt256 DefaultBlobGasPriceUpdateFraction { get; private set; } = 3338477;

    /// <summary>
    /// Gets the <c>MIN_BLOB_GASPRICE</c> parameter, in wei.
    /// </summary>
    /// <remarks>Defaults to 1, or 2^25 after Eip7762.</remarks>
    public static UInt256 GetMinBlobGasPrice(IReleaseSpec releaseSpec)
    {
        if (OveriddenMinBlobGasPrice.HasValue)
        {
            return OveriddenMinBlobGasPrice.Value;
        }
        else
        {
            return releaseSpec.IsEip7762Enabled ? Eip7762MinBlobGasPrice : 1;
        }
    }


    // The parameter mutators are kept separate deliberately to ensure no accidental value changes.
    public static void OverrideIfAny(UInt256? minBlobGasPrice = null)
    {
        if (minBlobGasPrice.HasValue)
            OveriddenMinBlobGasPrice = minBlobGasPrice.Value;
    }
}
