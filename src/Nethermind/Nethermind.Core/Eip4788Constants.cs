// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Represents the <see href="https://eips.ethereum.org/EIPS/eip-4788#specification">EIP-4788</see> parameters.
/// </summary>
public static class Eip4788Constants
{
    public const string ContractAddressKey = "BEACON_ROOTS_ADDRESS";

    /// <summary>
    /// Gets the <c>BEACON_ROOTS_ADDRESS</c> parameter.
    /// </summary>
    public static readonly Address BeaconRootsAddress = new("0x000F3df6D732807Ef1319fB7B8bB8522d0Beac02");

    /// <summary>
    /// The <c>HISTORY_SERVE_WINDOW</c> parameter.
    /// </summary>
    public static readonly ulong RingBufferSize = 8191;
}
