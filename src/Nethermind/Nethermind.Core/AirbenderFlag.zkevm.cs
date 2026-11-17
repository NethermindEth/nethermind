// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>Whether the guest runs on Airbender and uses what only Airbender offers.</summary>
/// <remarks>
/// <see langword="false"/> as written; the Airbender guest's <c>substitutions.xml</c> stubs <see cref="IsActive"/> to
/// <see langword="true"/> at link time and ILC folds every check on it.
/// </remarks>
public readonly struct AirbenderFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
