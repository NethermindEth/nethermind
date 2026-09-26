// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Evm.CodeAnalysis;

namespace Nethermind.Evm;

public ref partial struct EvmStack
{
    /// <summary>A copy of <paramref name="frame"/> with the head and code guest dispatch carries in registers.</summary>
    internal EvmStack(in EvmStack frame, nint head, ref byte code)
    {
        this = frame;
        Head = head;
        Code = ref code;
    }

    /// <summary>Reports whether <paramref name="destination"/> is a valid jump destination in <see cref="Code"/>.</summary>
    /// <remarks>
    /// The code info's incremental bitmap holds only the destinations analyzed so far, so a clear bit is not yet
    /// an answer: it falls through to <see cref="CodeInfo.AnalyzeJump"/>, which analyzes the destination.
    /// Repeat jumps to an already analyzed destination take the bit test alone. See
    /// <c>EvmStack.std.cs</c> for the host form, which analyzes the whole code up front.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsJumpDestination(int destination)
    {
        if ((uint)destination >= (uint)CodeLength) return false;
        return JumpDestinationAnalyzer.IsJumpDestination(_jumpDestinations!, destination) || AnalyzeJumpDestination(destination);
    }

    /// <summary>Analyzes <paramref name="destination"/>, a position inside <see cref="Code"/> whose bit is still clear, and reports whether it is a jump destination.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool AnalyzeJumpDestination(int destination) =>
        _codeInfo is not null && _codeInfo.AnalyzeJump(destination, _jumpDestinations!, MemoryMarshal.CreateReadOnlySpan(ref Code, (int)CodeLength));

    /// <summary>
    /// Marks <paramref name="destination"/>, a position inside <see cref="Code"/> whose bit is still clear, when a single
    /// look-back proves it a jump destination, and reports whether it did.
    /// </summary>
    /// <remarks>
    /// A false answer leaves the destination to <see cref="AnalyzeJumpDestination"/>. The bitmap is reached only once
    /// the destination is proven, so a frameless handler does not hold it through the look-back.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly bool TryMarkJumpDestination(nint destination)
    {
        if (_codeInfo is null || !_codeInfo.IsJumpProvenByLookBack(destination, ref Code)) return false;

        ref long segment = ref Unsafe.Add(ref _jumpDestinationBits, destination >> 6);
        segment |= 1L << (int)destination;
        return true;
    }

    /// <summary>Reports whether <paramref name="destination"/> is a jump destination already analyzed.</summary>
    /// <remarks>
    /// A bit test and nothing else, so a false answer may only mean "not analyzed yet". The fused PUSH2+JUMP
    /// fuses only on a true answer and otherwise runs the two unfused, leaving the scan to the jump handler:
    /// carrying the scan inline made the PUSH2 handler save and restore the callee-saved registers on every
    /// execution, though almost none of them scan.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsKnownJumpDestination(int destination) =>
        (nuint)(uint)destination < (nuint)CodeLength && IsAnalyzedJumpDestination((uint)destination);

    /// <summary>Reports whether <paramref name="destination"/>, a position inside <see cref="Code"/>, is a jump destination already analyzed.</summary>
    /// <remarks>
    /// The bitmap is sized for the code, so a position inside it needs no bounds check of its own. The bit is
    /// tested in the sign, which takes one instruction fewer than masking it: a narrowed mask makes the JIT
    /// sign-extend it first.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly bool IsAnalyzedJumpDestination(nuint destination)
    {
        Debug.Assert(destination < (nuint)CodeLength, "Only a position inside the code indexes the bitmap unchecked.");
        ulong bits = (ulong)Unsafe.Add(ref _jumpDestinationBits, (nint)(destination >> 6));
        return (long)((bits >> (int)destination) << 63) < 0;
    }

    /// <summary>The slot at <paramref name="index"/>, counted up from the bottom of the stack, for callers that have bounded the index.</summary>
    /// <remarks>
    /// Guest dispatch carries the head outside <see cref="Head"/> (see <c>VirtualMachine.Dispatch.zkevm.cs</c>), so its
    /// handlers address slots by the head they hold rather than through <see cref="PeekBytesByRefUnchecked()"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly ref byte SlotUnchecked(nint index) => ref Unsafe.Add(ref _stack, index * WordSize);

    /// <summary>The first word of <see cref="_jumpDestinations"/>, which the bit test indexes without a null check.</summary>
    private ref long _jumpDestinationBits;

    // Resolved when the stack is built, as the host form is: resolving on the first jump put a call and a
    // write barrier into every handler that validates a jump. A stack over code without its code info gets
    // an empty bitmap sized for that code, so the unchecked bit test stays inside it and rejects everything.
    partial void InitializeJumpDestinations()
    {
        long[] bitmap = CodeLength == 0
            ? JumpDestinationAnalyzer.EmptyBitmap
            : _codeInfo?.IncrementalJumpBitmap ?? JumpDestinationAnalyzer.CreateBitmap((int)CodeLength);
        _jumpDestinations = bitmap;
        _jumpDestinationBits = ref MemoryMarshal.GetArrayDataReference(bitmap);
    }
}
