// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Evm.CodeAnalysis;

public sealed partial class CodeInfo
{
    private long[]? _incrementalJumpBitmap;
    private nint _analyzedUntil;

    /// <summary>The jump-destination bitmap of this code, populated only as far as the scan has reached.</summary>
    /// <remarks>
    /// Sized for the whole code so the shared bit test can index it, but a clear bit only means "not a
    /// destination, or not scanned yet"; <see cref="AnalyzeJump"/> is what turns that into an answer.
    /// </remarks>
    internal long[] IncrementalJumpBitmap => _incrementalJumpBitmap ??= JumpDestinationAnalyzer.CreateBitmap(Code.Length);

    /// <summary>Extends the scan far enough to decide <paramref name="destination"/>, and reports whether it is a jump destination.</summary>
    /// <remarks>
    /// The guest pays for every byte it scans, and a frame typically jumps into a prefix of the code, so
    /// the scan advances only to the furthest destination asked for rather than running to the end. A
    /// destination the scan has already passed rescans nothing.
    /// </remarks>
    internal bool AnalyzeJump(int destination)
    {
        if (CodeSpan[destination] != (byte)Instruction.JUMPDEST) return false;
        long[] bitmap = IncrementalJumpBitmap;
        _analyzedUntil = (nint)JumpDestinationAnalyzer.ScanUntil((nuint)_analyzedUntil, destination, bitmap, CodeSpan);
        return JumpDestinationAnalyzer.IsJumpDestination(bitmap, destination);
    }
}
