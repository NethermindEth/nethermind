// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Represents the <see href="https://eips.ethereum.org/EIPS/eip-3074#constants">EIP-3074</see> parameters.
/// </summary>
public class Eip3074Constants
{
    /// <summary>
    /// Used to prevent signature collision with other signing formats
    /// </summary>
    public const byte AuthMagic = 0x04;
}
