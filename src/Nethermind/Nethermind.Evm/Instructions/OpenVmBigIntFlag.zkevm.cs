// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm;

/// <summary>Whether the guest's zkVM runs MUL on OpenVM's 256-bit integer (bigint) extension.</summary>
/// <remarks>
/// Only OpenVM's runtime exports <c>zkvm_u256_mul</c>, so the choice cannot be a compile-time symbol here.
/// <see cref="IsActive"/> is <see langword="false"/> as written; the OpenVM guest's <c>substitutions.xml</c> stubs it
/// to <see langword="true"/> at link time. ILC then folds every check, so the OpenVM guest calls the extension
/// directly and the other guests never reference its symbol.
/// </remarks>
internal readonly struct OpenVmBigIntFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
