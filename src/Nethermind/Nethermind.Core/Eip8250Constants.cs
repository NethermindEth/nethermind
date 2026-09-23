// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

/// <summary><see href="https://eips.ethereum.org/EIPS/eip-8250">EIP-8250</see> (Keyed Nonces) parameters.</summary>
public static class Eip8250Constants
{
    public const int MaxNonceKeys = 16;
    public const ulong MaxNonceSeq = ulong.MaxValue;
    public const long KeyedNonceFirstUseGas = 20_000;

    // Provisional: spec address is TBD; mirrors the only existing implementation.
    public static readonly Address NonceManagerAddress = new("0x0000000000000000000000000000000000008250");

    // Spec-pinned revert(0, 0): a storage namespace only, never callable.
    public static ReadOnlySpan<byte> NonceManagerCode => [0x60, 0x00, 0x60, 0x00, 0xfd];
}
