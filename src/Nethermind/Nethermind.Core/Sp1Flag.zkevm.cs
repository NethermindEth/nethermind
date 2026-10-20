// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>Whether the guest runs on SP1 and uses what only SP1 offers.</summary>
/// <remarks>
/// <see langword="false"/> as written; the SP1 guest's <c>substitutions.xml</c> stubs <see cref="IsActive"/> to
/// <see langword="true"/> at link time and ILC folds every check on it, so the other guests never reference the
/// symbols only SP1's runtime exports.
/// </remarks>
public readonly struct Sp1Flag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
