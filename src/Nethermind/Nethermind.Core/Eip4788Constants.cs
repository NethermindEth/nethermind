// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Core;

/// <summary>
/// Represents the <see href="https://eips.ethereum.org/EIPS/eip-4788#specification">EIP-4788</see> parameters.
/// </summary>
public static class Eip4788Constants
{
    /// <summary>
    /// Gets the <c>BEACON_ROOTS_ADDRESS</c> parameter.
    /// </summary>
    public static readonly Address BeaconRootsAddress = new("0x000F3df6D732807Ef1319fB7B8bB8522d0Beac02");

    /// <summary>
    /// Gets the <c>HISTORY_BUFFER_LENGTH</c> parameter.
    /// </summary>
    public static readonly UInt256 HistoryBufferLength = 8191;
}
