// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.CodeAnalysis;

/// <summary>Whether the guest's zkVM analyzes jump destinations with ZisK's JUMPDEST bitmap precompile.</summary>
/// <remarks>
/// The three guests link one managed closure, and only the ZisK guest links <c>zisk_jump_dest_bitmap</c>, so the
/// choice cannot be a compile-time symbol here. <see cref="IsActive"/> is <see langword="false"/> as written; the
/// ZisK guest's <c>substitutions.xml</c> stubs it to <see langword="true"/> at link time. ILC then folds the check,
/// so the ZisK guest calls the precompile directly and the other guests never reference its symbol.
/// </remarks>
internal readonly struct ZiskJumpDestFlag
{
    public static bool IsActive => false;
}
