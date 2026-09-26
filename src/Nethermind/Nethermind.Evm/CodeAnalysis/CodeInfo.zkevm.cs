// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm.CodeAnalysis;

public sealed partial class CodeInfo
{
    /// <summary>The number of zero bytes that follow every non-empty code in memory.</summary>
    /// <remarks>
    /// A PUSH32 in the last byte reads 32 immediate bytes, and the next opcode read then lands on the last
    /// padding byte. Zero is STOP, so dispatch that runs off the end halts as the implicit STOP would.
    /// </remarks>
    internal const int DispatchPadding = 33;

    /// <remarks>
    /// Always copies, as a caller's buffer promises nothing about the bytes after the code. This constructor is
    /// the only way code reaches a <see cref="CodeInfo"/>, so it covers every source: a witness entry, a deployed
    /// code, a delegation designator, and init code taken from a transaction or from memory.
    /// See <see cref="DispatchFlags.PaddedCode"/>.
    /// </remarks>
    static partial void PadForDispatch(ref ReadOnlyMemory<byte> code)
    {
        if (code.IsEmpty)
            return;

        byte[] padded = GC.AllocateUninitializedArray<byte>(code.Length + DispatchPadding);
        code.Span.CopyTo(padded);
        padded.AsSpan(code.Length).Clear();
        code = padded.AsMemory(0, code.Length);
    }

    // Guest execution is single-threaded; bitmap writes and the resume cursor are not synchronized.
    private long[]? _incrementalJumpBitmap;
    private nint _analyzedUntil;

    /// <summary>The jump-destination bitmap of this code, holding only the destinations analyzed so far.</summary>
    /// <remarks>
    /// Sized for the whole code so the shared bit test can index it, but a clear bit only means "not a
    /// destination, or not analyzed yet"; <see cref="AnalyzeJump"/> is what turns that into an answer.
    /// </remarks>
    internal long[] IncrementalJumpBitmap => _incrementalJumpBitmap ??= JumpDestinationAnalyzer.CreateBitmap(Code.Length);

    /// <summary>Extends the scan far enough to decide <paramref name="destination"/>, and reports whether it is a jump destination.</summary>
    /// <param name="destination">A destination inside the code.</param>
    /// <param name="bitmap">This code's <see cref="IncrementalJumpBitmap"/>.</param>
    /// <param name="code">This code.</param>
    /// <remarks>
    /// The guest pays for every byte it scans, so most destinations are proven by looking at the 32 bytes before
    /// them, and the rest are scanned from the nearest instruction start found that way rather than from the
    /// start of the code (see <see cref="JumpDestinationAnalyzer.AnalyzeJump"/>). The caller passes the bitmap
    /// and code it already holds.
    /// </remarks>
    internal bool AnalyzeJump(int destination, long[] bitmap, ReadOnlySpan<byte> code) =>
        code[0] != (byte)Instruction.STOP && code[destination] == (byte)Instruction.JUMPDEST &&
        JumpDestinationAnalyzer.AnalyzeJump(destination, bitmap, code, ref _analyzedUntil);

    /// <summary>Reports whether a single look-back proves <paramref name="destination"/> a jump destination.</summary>
    /// <param name="destination">A destination inside the code.</param>
    /// <param name="code">The first byte of this code.</param>
    /// <remarks>
    /// The part of <see cref="AnalyzeJump"/> that needs no call and no bounds check, so a frameless handler can take
    /// it; the caller marks a proven destination. A false answer leaves the destination to <see cref="AnalyzeJump"/>.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsJumpProvenByLookBack(nint destination, ref byte code) =>
        code != (byte)Instruction.STOP && Unsafe.Add(ref code, destination) == (byte)Instruction.JUMPDEST &&
        JumpDestinationAnalyzer.IsProvenByLookBack(destination, ref code, _analyzedUntil);
}
