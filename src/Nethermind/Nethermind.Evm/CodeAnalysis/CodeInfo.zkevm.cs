// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm.CodeAnalysis;

public sealed partial class CodeInfo
{
    private ReadOnlyMemory<byte> _code;

    /// <remarks>
    /// Copies unless the code is an <see cref="ExecutableCodeMemory"/> buffer, as any other buffer promises nothing
    /// about the bytes after the code. Padding here rather than on first execution keeps <see cref="Code"/> a plain
    /// field read, which the guest pays for on every code access.
    /// </remarks>
    partial void InitializeCode(ReadOnlyMemory<byte> code) =>
        _code = code.IsEmpty ? code
            : ExecutableCodeMemory.TryGetExecutionBuffer(code, out byte[]? buffer) ? buffer.AsMemory(0, code.Length)
            : CreatePaddedCode(code.Span).AsMemory(0, code.Length);

    public partial ReadOnlyMemory<byte> Code => _code;

    public partial ReadOnlySpan<byte> CodeSpan => _code.Span;

    internal partial int CodeLength => _code.Length;

    internal partial ReadOnlySpan<byte> ExecutionCodeSpan => _code.Span;

    // Guest execution is single-threaded; bitmap writes and the resume cursor are not synchronized.
    private long[]? _incrementalJumpBitmap;
    private nint _analyzedUntil;

    /// <summary>The jump-destination bitmap of this code, holding only the destinations analyzed so far.</summary>
    /// <remarks>
    /// Sized for the whole code so the shared bit test can index it, but a clear bit only means "not a
    /// destination, or not analyzed yet"; <see cref="AnalyzeJump"/> is what turns that into an answer.
    /// </remarks>
    internal long[] IncrementalJumpBitmap => _incrementalJumpBitmap ??= JumpDestinationAnalyzer.CreateBitmap(CodeLength);

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

    /// <summary>Reports whether a single look-back proves <paramref name="destination"/> the destination of the jump running.</summary>
    /// <param name="destination">A destination inside the code.</param>
    /// <param name="code">The first byte of this code.</param>
    /// <remarks>
    /// The part of <see cref="AnalyzeJump"/> that needs no call and no bounds check, so a frameless handler can take
    /// it; the caller marks a proven destination. A false answer leaves the destination to <see cref="AnalyzeJump"/>.
    /// Code that starts with STOP halts before any jump, so unlike <see cref="AnalyzeJump"/> this does not test for it.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsJumpProvenByLookBack(nint destination, ref byte code) =>
        Unsafe.Add(ref code, destination) == (byte)Instruction.JUMPDEST &&
        JumpDestinationAnalyzer.IsProvenByLookBack(destination, ref code, _analyzedUntil);
}
