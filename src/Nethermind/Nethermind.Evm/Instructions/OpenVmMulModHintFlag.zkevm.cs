// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm;

/// <summary>Whether the guest takes MULMOD's quotient and remainder from its runner as a hint it checks.</summary>
/// <remarks>
/// Only the OpenVM guest's runtime exports the hint and its check, so the choice cannot be a
/// compile-time symbol here. <see cref="IsActive"/> is <see langword="false"/> as written; the OpenVM guest's
/// <c>substitutions.xml</c> stubs it to <see langword="true"/> at link time. ILC then folds every check, so the
/// other guests never reference the symbols.
/// </remarks>
internal readonly struct OpenVmMulModHintFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
