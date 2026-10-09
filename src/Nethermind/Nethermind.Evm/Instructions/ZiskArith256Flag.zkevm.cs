// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm;

/// <summary>Whether the guest's zkVM runs DIV, MOD, ADDMOD and MULMOD on ZisK's 256-bit arithmetic.</summary>
/// <remarks>
/// The three guests link one managed closure, and only ZisK's runtime exports these routines, so the choice
/// cannot be a compile-time symbol here. <see cref="IsActive"/> is <see langword="false"/> as written; the ZisK
/// guest's <c>substitutions.xml</c> stubs it to <see langword="true"/> at link time. ILC then folds every
/// check, so the ZisK guest calls the routines directly and the other guests never reference their symbols.
/// </remarks>
internal readonly struct ZiskArith256Flag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
