// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Represents the <see href="https://eips.ethereum.org/EIPS/eip-4788#specification">EIP-4788</see> parameters.
/// </summary>
public static partial class Eip4788Constants
{
    public const string ContractAddressKey = "BEACON_ROOTS_ADDRESS";

    /// <summary>
    /// The <c>HISTORY_SERVE_WINDOW</c> parameter.
    /// </summary>
    public static readonly ulong RingBufferSize = 8191;
}
