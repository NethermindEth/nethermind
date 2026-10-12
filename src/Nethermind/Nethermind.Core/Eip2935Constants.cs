// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Represents the <see href="https://eips.ethereum.org/EIPS/eip-2935#specification">EIP-2935</see> parameters.
/// </summary>
public static partial class Eip2935Constants
{
    public const string ContractAddressKey = "HISTORY_STORAGE_ADDRESS";

    /// <summary>
    /// The <c>HISTORY_SERVE_WINDOW</c> parameter.
    /// </summary>
    public static readonly ulong RingBufferSize = 8191;
}
