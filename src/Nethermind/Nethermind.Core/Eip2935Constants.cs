// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Represents the <see href="https://eips.ethereum.org/EIPS/eip-2935#specification">EIP-2935</see> parameters.
/// </summary>
public static class Eip2935Constants
{
    public const string ContractAddressKey = "HISTORY_STORAGE_ADDRESS";

    /// <summary>
    /// The <c>HISTORY_STORAGE_ADDRESS</c> parameter.
    /// </summary>
    public static readonly Address BlockHashHistoryAddress = new("0x0000F90827F1C53a10cb7A02335B175320002935");

    /// <summary>
    /// The <c>HISTORY_SERVE_WINDOW</c> parameter.
    /// </summary>
    public static readonly long RingBufferSize = 8191;
}
