// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>Whether the guest runs on ZisK and uses what only ZisK offers.</summary>
/// <remarks>
/// <see langword="false"/> as written; the ZisK guest's <c>substitutions.xml</c> stubs <see cref="IsActive"/> to
/// <see langword="true"/> at link time and ILC folds every check on it. The direct <c>memmove</c> and <c>memset</c>
/// calls it enables pass unpinned pointers into managed memory, which is sound only in the single-threaded guest.
/// </remarks>
public readonly struct ZiskFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
