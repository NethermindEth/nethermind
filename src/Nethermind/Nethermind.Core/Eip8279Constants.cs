// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Represents the <see href="https://eips.ethereum.org/EIPS/eip-8279">EIP-8279</see>
/// (block access list byte floor) parameters.
/// </summary>
public static class Eip8279Constants
{
    public const ulong FloorGasPerByte = 64;
    public const ulong BalBytesPerAddress = 20;
    public const ulong BalBytesPerStorageKey = 32;
    public const ulong BalBytesPerStorageValue = 32;
    public const ulong BalBytesPerNonce = 8;
    public const ulong DelegationCodeBytes = 23;

    /// <summary>
    /// BAL bytes statically attributable to one EIP-7702 authorization tuple:
    /// authority address + delegation designator code + nonce.
    /// </summary>
    public const ulong AuthorizationBalBytes = BalBytesPerAddress + DelegationCodeBytes + BalBytesPerNonce;
}
