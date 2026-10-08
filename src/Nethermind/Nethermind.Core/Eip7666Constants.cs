// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Precompiles;

namespace Nethermind.Core;

/// <summary><see href="https://eips.ethereum.org/EIPS/eip-7666">EIP-7666</see> (EVM-ify the identity precompile) parameters.</summary>
public static class Eip7666Constants
{
    /// <summary><c>IDENTITY_PRECOMPILE_ADDRESS</c>: the retired identity precompile's address.</summary>
    public static readonly Address IdentityAddress = PrecompiledAddresses.Identity;

    /// <summary><c>EVM_CODE</c>, installed at <see cref="IdentityAddress"/> by the fork block.</summary>
    /// <remarks><c>CALLDATASIZE PUSH0 PUSH0 CALLDATACOPY CALLDATASIZE PUSH0 RETURN</c>; needs EIP-3855 (PUSH0).</remarks>
    public static ReadOnlyMemory<byte> IdentityCode { get; } = new byte[] { 0x36, 0x5f, 0x5f, 0x37, 0x36, 0x5f, 0xf3 };
}
