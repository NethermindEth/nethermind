// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Represents the <see href="https://eips.ethereum.org/EIPS/eip-7709#specification">EIP-7709</see> parameters.
/// </summary>
public static class Eip7709Constants
{
    /// <summary>
    /// The <c>BLOCKHASH_SERVE_WINDOW</c> parameter: BLOCKHASH keeps serving only the most
    /// recent 256 ancestors even though the EIP-2935 ring buffer retains more.
    /// </summary>
    public const ulong BlockHashServeWindow = 256;
}
