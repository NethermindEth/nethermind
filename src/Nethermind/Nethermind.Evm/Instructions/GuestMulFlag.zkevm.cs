// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Evm;

/// <summary>Whether the guest links a native MUL handler, <see cref="GuestMul.MultiplyAndDispatch"/>.</summary>
/// <remarks>
/// Only the SP1 and OpenVM guests assemble one, so the choice cannot be a compile-time symbol here.
/// <see cref="IsActive"/> is <see langword="false"/> as written; those guests' <c>substitutions.xml</c> stub it to
/// <see langword="true"/> at link time. ILC then folds every check, so the other guests never reference the symbol.
/// </remarks>
internal readonly struct GuestMulFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
