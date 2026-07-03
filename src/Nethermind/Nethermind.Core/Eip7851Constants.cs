// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core;

/// <summary>
/// Represents the <see href="https://eips.ethereum.org/EIPS/eip-7851">EIP-7851</see>
/// (code-controlled EOA delegation) parameters.
/// </summary>
public static class Eip7851Constants
{
    private static readonly byte[] _delegationHeader = [0xef, 0x01, 0x01];

    /// <summary>
    /// The ECDSA-disabled delegation designator prefix. Accounts whose code is exactly
    /// <c>0xef0101 || delegate_address</c> can no longer authorize transactions or EIP-7702
    /// delegation changes with their ECDSA key; only the delegated wallet code can update the
    /// delegation via SETSELFDELEGATE.
    /// </summary>
    public static ReadOnlySpan<byte> DelegationHeader => _delegationHeader.AsSpan();

    public static bool IsEcdsaDisabledDelegatedCode(ReadOnlySpan<byte> code) =>
        code.Length == _delegationHeader.Length + Address.Size
        && DelegationHeader.SequenceEqual(code[.._delegationHeader.Length]);
}
