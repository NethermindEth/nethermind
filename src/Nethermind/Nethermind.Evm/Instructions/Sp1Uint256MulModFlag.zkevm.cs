// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm;

/// <summary>Whether the guest's zkVM runs MULMOD on SP1's 256-bit modular multiplication precompile.</summary>
/// <remarks>
/// Only SP1's runtime exports <c>zkvm_uint256_mulmod</c>, so the choice cannot be a compile-time symbol here.
/// <see cref="IsActive"/> is <see langword="false"/> as written; the SP1 guest's <c>substitutions.xml</c> stubs it
/// to <see langword="true"/> at link time. ILC then folds every check, so the SP1 guest calls the precompile
/// directly and the other guests never reference its symbol.
/// </remarks>
internal readonly struct Sp1Uint256MulModFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
