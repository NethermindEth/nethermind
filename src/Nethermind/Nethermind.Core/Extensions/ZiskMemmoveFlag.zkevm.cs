// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Extensions;

/// <summary>Whether the guest calls its zkVM's <c>memmove</c> directly for byte-run copies.</summary>
/// <remarks>
/// The three guests link one managed closure, and only the ZisK guest has been measured and run with the direct
/// call (its runtime turns <c>memmove</c> into a precompile), so the choice cannot be a compile-time symbol here.
/// <see cref="IsActive"/> is <see langword="false"/> as written; the ZisK guest's <c>substitutions.xml</c> stubs it
/// to <see langword="true"/> at link time. ILC then folds the check, so the other guests and the zkEVM test hosts
/// keep corelib's copy and never reference the import.
/// </remarks>
internal readonly struct ZiskMemmoveFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
