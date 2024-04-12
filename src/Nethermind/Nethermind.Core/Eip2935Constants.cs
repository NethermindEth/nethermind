// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>
/// Represents the <see href="https://eips.ethereum.org/EIPS/eip-2935#specification">EIP-4788</see> parameters.
/// </summary>
public static class Eip2935Constants
{
    /// <summary>
    /// Gets the <c>HISTORY_STORAGE_ADDRESS</c> parameter.
    /// </summary>
    public static readonly Address BlockHashHistoryAddress = new("0xfffffffffffffffffffffffffffffffffffffffe");

    public static readonly long RingBufferSize = 256;
}
