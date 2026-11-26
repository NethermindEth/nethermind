// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Constants for EIP-8131: Unified Transaction Content Floor.
/// </summary>
public static class Eip8131Constants
{
    /// <summary>
    /// Floor gas charged per user-controlled transaction content byte.
    /// </summary>
    public const ulong FloorGasPerByte = 64;

    /// <summary>
    /// Content bytes counted per EIP-7702 authorization tuple (its worst-case RLP size).
    /// </summary>
    public const ulong AuthorizationTupleBytes = 108;
}
