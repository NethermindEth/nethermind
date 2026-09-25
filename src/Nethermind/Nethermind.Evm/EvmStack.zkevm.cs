// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

public ref partial struct EvmStack
{
    /// <summary>Reports whether <paramref name="destination"/> is a valid jump destination in <see cref="Code"/>.</summary>
    /// <remarks>
    /// The code info's incremental bitmap covers only the code scanned so far, so a clear bit is not yet an
    /// answer: it falls through to <see cref="CodeInfo.AnalyzeJump"/>, which extends the scan to reach the
    /// destination. Repeat jumps to an already scanned destination take the bit test alone. See
    /// <c>EvmStack.std.cs</c> for the host form, which analyzes the whole code up front.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsJumpDestination(int destination)
    {
        if ((uint)destination >= (uint)CodeLength) return false;
        long[] bitmap = _jumpDestinations!;
        return JumpDestinationAnalyzer.IsJumpDestination(bitmap, destination)
            || (_codeInfo is not null && _codeInfo.AnalyzeJump(destination, bitmap, MemoryMarshal.CreateReadOnlySpan(ref Code, (int)CodeLength)));
    }

    /// <summary>Reports whether <paramref name="destination"/> is a jump destination the scan has already reached.</summary>
    /// <remarks>
    /// A bit test and nothing else, so a false answer may only mean "not scanned yet". The fused PUSH2+JUMP
    /// fuses only on a true answer and otherwise runs the two unfused, leaving the scan to the jump handler:
    /// carrying the scan inline made the PUSH2 handler save and restore the callee-saved registers on every
    /// execution, though almost none of them scan.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsKnownJumpDestination(int destination) =>
        (uint)destination < (uint)CodeLength && JumpDestinationAnalyzer.IsJumpDestination(_jumpDestinations!, destination);

    // Resolved when the stack is built, as the host form is: resolving on the first jump put a call and a
    // write barrier into every handler that validates a jump.
    partial void InitializeJumpDestinations() =>
        _jumpDestinations = CodeLength == 0
            ? JumpDestinationAnalyzer.EmptyBitmap
            : _codeInfo?.IncrementalJumpBitmap ?? JumpDestinationAnalyzer.EmptyBitmap;
}
