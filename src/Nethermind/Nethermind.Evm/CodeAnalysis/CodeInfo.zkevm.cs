// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
    /// On ZisK the precompile analyzes the whole code when the bitmap is created, so there a clear bit is the answer.
    /// </remarks>
    internal long[] IncrementalJumpBitmap => _incrementalJumpBitmap ??= CreateJumpBitmap();

    private long[] CreateJumpBitmap()
    {
        long[] bitmap = JumpDestinationAnalyzer.CreateBitmap(CodeLength);
        // STOP-first code halts before any jump, so it is not worth analyzing.
        if (ZiskJumpDestFlag.IsActive && CodeLength != 0 && _code.Span[0] != (byte)Instruction.STOP) AnalyzeWithPrecompile(bitmap);
        return bitmap;
    }

    /// <summary>Marks every jump destination of this code in <paramref name="bitmap"/> with ZisK's JUMPDEST bitmap precompile.</summary>
    /// <remarks>
    /// The precompile takes two steps and a cost linear in the code length, which real code repays in lazy
    /// analysis: deferring it to the first destination the look-back cannot decide skips too few codes to pay
    /// for the look-backs. Moving the cursor to the end leaves every lazy path a plain bit test. It reads and
    /// writes whole aligned words, reading at most 7 bytes past the code, which the execution padding holds; an
    /// unaligned buffer keeps the lazy analysis, as the precompile cannot be proven on it.
    /// </remarks>
    private unsafe void AnalyzeWithPrecompile(long[] bitmap)
    {
        fixed (byte* code = _code.Span)
        fixed (long* bits = bitmap)
        {
            if ((((nuint)code | (nuint)bits) & (sizeof(long) - 1)) != 0) return;
            ZiskJumpDestBitmap(bits, code, (ulong)CodeLength);
        }

        _analyzedUntil = CodeLength;
    }

    /// <summary>ZisK's JUMPDEST bitmap precompile, from the guest's <c>jump_dest.S</c>.</summary>
    [DllImport("__Internal", EntryPoint = "zisk_jump_dest_bitmap", ExactSpelling = true), SuppressGCTransition]
    private static extern unsafe void ZiskJumpDestBitmap(long* bitmap, byte* code, ulong size);

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
