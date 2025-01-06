// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Represents the <see href="https://eips.ethereum.org/EIPS/eip-2935#specification">EIP-2935</see> parameters.
/// </summary>
public static class Eip2935Constants
{
    /// <summary>
    /// The <c>HISTORY_STORAGE_ADDRESS</c> parameter.
    /// </summary>
    public static readonly Address BlockHashHistoryAddress = new("0x0f792be4b0c0cb4dae440ef133e90c0ecd48cccc");

    /// <summary>
    /// The <c>HISTORY_SERVE_WINDOW</c> parameter.
    /// </summary>
    public static readonly long RingBufferSize = 8192;
}
