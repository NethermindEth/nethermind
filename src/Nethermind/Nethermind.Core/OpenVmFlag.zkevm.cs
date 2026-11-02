// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>Whether the guest runs on OpenVM and uses what only OpenVM offers.</summary>
/// <remarks>
/// <see langword="false"/> as written; the OpenVM guest's <c>substitutions.xml</c> stubs <see cref="IsActive"/> to
/// <see langword="true"/> at link time and ILC folds every check on it, so the other guests never reference the
/// symbols only OpenVM's runtime exports. The direct <c>memmove</c> calls it enables pass unpinned pointers into
/// managed memory, which is sound only in the single-threaded guest.
/// </remarks>
public readonly struct OpenVmFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
