// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

public ref partial struct EvmStack
{
    /// <summary>Reports whether <paramref name="destination"/> is a valid jump destination in <see cref="Code"/>.</summary>
    /// <remarks>
    /// The bitmap is resolved when the stack is built (<see cref="InitializeJumpDestinations"/>), so a jump
    /// validates against the frame it is executing without walking <c>vm.VmState.Env.CodeInfo</c>, and
    /// the check itself has no call in it. Resolving on the first jump instead put a call to the
    /// analyzer and a write barrier into every handler that validates a jump (JUMP, JUMPI and the fused
    /// PUSH2+JUMP), and the JIT then saved and restored the callee-saved registers on every execution of
    /// those handlers. Only a stack built over no code may omit the code info; the empty bitmap then
    /// rejects every destination. The zkEVM guest resolves it the same way. It used to scan the code only
    /// as far as each jump reached, which kept that call, and the register saves, in the same handlers:
    /// on mainnet blocks the scan it saved was worth less than half the saves it cost.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsJumpDestination(int destination)
    {
        Debug.Assert(_codeInfo is not null || CodeLength == 0, "A stack that executes code must carry that code's CodeInfo.");
        long[]? bitmap = _jumpDestinations;
        // Null only for a default-constructed stack, which has no code to jump into.
        return bitmap is not null && JumpDestinationAnalyzer.IsJumpDestination(bitmap, destination);
    }

    // A stack built over code without its CodeInfo gets the empty bitmap, which rejects every destination;
    // the Debug assertion in IsJumpDestination flags that case.
    partial void InitializeJumpDestinations() =>
        _jumpDestinations = CodeLength == 0
            ? JumpDestinationAnalyzer.EmptyBitmap
            : _codeInfo?.JumpDestinationBitmap ?? JumpDestinationAnalyzer.EmptyBitmap;
}
