// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

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
    // 0x000F3df6D732807Ef1319fB7B8bB8522d0Beac02
    public static readonly Address BeaconRootsAddress = new((ReadOnlySpan<byte>)[0x00, 0x0f, 0x3d, 0xf6, 0xd7, 0x32, 0x80, 0x7e, 0xf1, 0x31, 0x9f, 0xb7, 0xb8, 0xbb, 0x85, 0x22, 0xd0, 0xbe, 0xac, 0x02]);

    /// <summary>
    /// The <c>HISTORY_SERVE_WINDOW</c> parameter.
    /// </summary>
    public static readonly ulong RingBufferSize = 8191;
}
