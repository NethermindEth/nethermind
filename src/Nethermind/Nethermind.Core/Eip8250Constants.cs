// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

/// <summary>
/// Represents the <see href="https://eips.ethereum.org/EIPS/eip-8250">EIP-8250</see> (Keyed Nonces) parameters.
/// </summary>
public static class Eip8250Constants
{
    /// <summary>Maximum number of nonce keys a single frame transaction may declare.</summary>
    public const int MaxNonceKeys = 16;

    /// <summary>Upper bound a nonce sequence must stay strictly below (2^64 - 1).</summary>
    public const ulong MaxNonceSeq = ulong.MaxValue;

    /// <summary>Gas charged the first time a non-zero nonce key is used, per key.</summary>
    public const long KeyedNonceFirstUseGas = 20_000;

    /// <summary>The <c>NONCE_MANAGER</c> system-contract address.</summary>
    /// <remarks>
    /// The spec address is TBD; this provisional value mirrors the only existing implementation (ethrex)
    /// pending ratification.
    /// </remarks>
    public static readonly Address NonceManagerAddress = new("0x0000000000000000000000000000000000008250");

    /// <summary>The <c>NONCE_MANAGER</c> deployed bytecode (<c>0x60006000fd</c>).</summary>
    /// <remarks>
    /// Runtime is <c>revert(0, 0)</c>: the contract is a storage namespace only and is never callable.
    /// Exposed as a <see cref="ReadOnlySpan{T}"/> so the shared bytecode cannot be mutated by callers.
    /// </remarks>
    public static ReadOnlySpan<byte> NonceManagerCode => [0x60, 0x00, 0x60, 0x00, 0xfd];
}
