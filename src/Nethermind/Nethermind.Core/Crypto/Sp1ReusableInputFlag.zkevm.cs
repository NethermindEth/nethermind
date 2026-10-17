// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Crypto;

/// <summary>Whether the guest's input lies in writable memory that the guest may reuse once the input is decoded.</summary>
/// <remarks>
/// <see cref="IsActive"/> is <see langword="false"/> as written; the SP1 guest's <c>substitutions.xml</c> stubs it
/// to <see langword="true"/> at link time, and ILC folds every check it gates.
/// </remarks>
internal readonly struct Sp1ReusableInputFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
