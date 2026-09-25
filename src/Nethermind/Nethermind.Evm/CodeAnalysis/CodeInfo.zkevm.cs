// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm.CodeAnalysis;

public sealed partial class CodeInfo
{
    // Guest execution is single-threaded; bitmap writes and the resume cursor are not synchronized.
    private long[]? _incrementalJumpBitmap;
    private nint _analyzedUntil;

    /// <summary>The jump-destination bitmap of this code, populated only as far as the scan has reached.</summary>
    /// <remarks>
    /// Sized for the whole code so the shared bit test can index it, but a clear bit only means "not a
    /// destination, or not scanned yet"; <see cref="AnalyzeJump"/> is what turns that into an answer.
    /// </remarks>
    internal long[] IncrementalJumpBitmap => _incrementalJumpBitmap ??= JumpDestinationAnalyzer.CreateBitmap(Code.Length);

    /// <summary>Extends the scan far enough to decide <paramref name="destination"/>, and reports whether it is a jump destination.</summary>
    /// <param name="destination">A destination inside the code.</param>
    /// <param name="bitmap">This code's <see cref="IncrementalJumpBitmap"/>.</param>
    /// <param name="code">This code.</param>
    /// <remarks>
    /// The guest pays for every byte it scans, and a frame typically jumps into a prefix of the code, so
    /// the scan stops at the first instruction boundary beyond the requested destination. A PUSH can
    /// overshoot it, but the resume cursor never splits an immediate or rewinds for an earlier query.
    /// The caller passes the bitmap and code it already holds.
    /// </remarks>
    internal bool AnalyzeJump(int destination, long[] bitmap, ReadOnlySpan<byte> code)
    {
        if (code[0] == (byte)Instruction.STOP || code[destination] != (byte)Instruction.JUMPDEST) return false;
        _analyzedUntil = (nint)JumpDestinationAnalyzer.ScanUntil((nuint)_analyzedUntil, destination, bitmap, code);
        return JumpDestinationAnalyzer.IsJumpDestination(bitmap, destination);
    }
}
