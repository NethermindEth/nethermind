// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>Whether the guest runs on ZisK and uses what only ZisK offers.</summary>
/// <remarks>
/// The three guests link one managed closure, so this cannot be a compile-time symbol. <see cref="IsActive"/> is
/// <see langword="false"/> as written; the ZisK guest's <c>substitutions.xml</c> stubs it to <see langword="true"/>
/// at link time, and ILC folds every check on it, so the other guests and the zkEVM test hosts never reference what
/// it gates. On ZisK it switches on:
/// <list type="bullet">
/// <item><description>the <c>memmove</c> and <c>memset</c> precompiles for byte-run copies and fills;</description></item>
/// <item><description>the JUMPDEST bitmap precompile, linked from the guest's <c>jump_dest.S</c>;</description></item>
/// <item><description>the 256-bit arithmetic routines behind DIV, MOD, ADDMOD and MULMOD;</description></item>
/// <item><description>Zbb's <c>rev8</c> for byte swaps.</description></item>
/// </list>
/// The direct <c>memmove</c> and <c>memset</c> calls pass unpinned pointers into managed memory, which is sound only
/// in a single-threaded guest whose GC cannot relocate objects between taking a pointer and the call returning.
/// </remarks>
public readonly struct ZiskFlag : IFlag
{
    /// <inheritdoc />
    public static bool IsActive => false;
}
