// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Constants for EIP-8279: Block Access List Byte Floor.
/// </summary>
public static class Eip8279Constants
{
    /// <summary>Block access list bytes contributed by an account address.</summary>
    public const ulong AddressBytes = 20;

    /// <summary>Block access list bytes contributed by a storage key.</summary>
    public const ulong StorageKeyBytes = 32;

    /// <summary>Block access list bytes contributed by a changed storage slot's post value.</summary>
    public const ulong StorageValueBytes = 32;

    /// <summary>Block access list bytes contributed by an account's post balance.</summary>
    public const ulong BalanceBytes = 32;

    /// <summary>Block access list bytes contributed by an account's post nonce.</summary>
    public const ulong NonceBytes = 8;

    /// <summary>Length of the EIP-7702 delegation designator an authorization writes.</summary>
    public const ulong DelegationCodeBytes = 23;

    /// <summary>
    /// Worst-case block access list bytes one EIP-7702 authorization contributes: the authority address,
    /// its delegation designator and its nonce.
    /// </summary>
    public const ulong AuthorizationBytes = AddressBytes + DelegationCodeBytes + NonceBytes;
}
