// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>Whether the guest's zkVM charges memory by the words a run first touches.</summary>
/// <remarks>
/// SP1 charges a word the first time a run touches it, far more than the instructions writing it, so where this is on
/// the guest reuses memory it has touched already rather than taking fresh memory. <see cref="IsActive"/> is
/// <see langword="false"/> as written; the SP1 guest's <c>substitutions.xml</c> stubs it to <see langword="true"/> at
/// link time, and ILC folds every check on it.
/// </remarks>
public readonly struct FirstTouchMemoryFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
