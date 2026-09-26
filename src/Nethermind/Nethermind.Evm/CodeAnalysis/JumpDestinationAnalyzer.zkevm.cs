// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Evm.CodeAnalysis;

public sealed partial class JumpDestinationAnalyzer
{
    // Guest execution is single-threaded; no cross-thread bitmap publication is needed.
    private long[]? _jumpDestinationBitmap = (codeInfo.Code.Length == 0 || skipAnalysis) ? _emptyJumpDestinationBitmap : null;

    /// <summary>The scan's two comparands, in the order it reads them: <c>JUMPDEST</c> then <c>PUSH1</c>.</summary>
    /// <remarks>
    /// ILC re-materialises a compared-against constant at every use inside a loop, and the preinitialiser
    /// folds <c>static readonly</c> scalars straight back into that. An array element is opaque to it, so
    /// reading these once before the scan keeps them in registers.
    /// </remarks>
    private static readonly int[] _byteScanThresholds = [JUMPDEST, PUSH1];

    /// <summary>The farthest a PUSH's immediates reach past its opcode: PUSH32's.</summary>
    private const int MaxImmediateLength = 32;

    /// <summary>How many look-backs a jump may take before it falls back to the contiguous scan.</summary>
    /// <remarks>
    /// With <see cref="MaxLocalScan"/>, bounds each jump's work: an unbounded search could walk far back to the
    /// same proven start for every new destination in crafted code, where the contiguous scan never covers a
    /// byte twice. Neither bound is reached on real code.
    /// </remarks>
    private const int MaxLookBacks = 16;

    /// <summary>How far before a destination the search may look for an instruction start to scan from.</summary>
    private const int MaxLocalScan = 256;

    /// <summary>The look-back's comparands: the high bit of each byte, then each window word's lane offsets plus one.</summary>
    /// <remarks>An array for the same reason as <see cref="_byteScanThresholds"/>: materialising each 64-bit constant takes several instructions.</remarks>
    private static readonly ulong[] _reachBiases =
    [
        0x8080_8080_8080_8080UL,
        0x0807_0605_0403_0201UL,
        0x100f_0e0d_0c0b_0a09UL,
        0x1817_1615_1413_1211UL,
        0x201f_1e1d_1c1b_1a19UL,
    ];

    /// <remarks>
    /// Zisk reaches every byte in four instructions, so the word scanner the std build keeps cannot
    /// win: it processes a word per PUSH rather than skipping one, and all real bytecode is PUSH-dense.
    /// </remarks>
    [SkipLocalsInit]
    internal static long[] PopulateJumpDestinationBitmap_Scalar(long[] bitmap, ReadOnlySpan<byte> code)
    {
        ProcessJumpDestinationBitmap_Byte(programCounter: 0, bitmap, code);

        return bitmap;
    }

    /// <remarks>
    /// Walks a moving pointer instead of a base plus an index: ILC recomputes the byte address on
    /// every step of the indexed form, and the position is only needed at the rare JUMPDEST. The
    /// indexed form is the faster one on x64, hence the split.
    /// </remarks>
    [SkipLocalsInit]
    private static unsafe nuint ProcessJumpDestinationBitmap_Byte(nuint programCounter, Span<long> bitmap, ReadOnlySpan<byte> code)
    {
        long currentFlags = 0;
        nuint flagsPosition = 0;
        ref int thresholds = ref MemoryMarshal.GetArrayDataReference(_byteScanThresholds);
        int jumpDest = thresholds;
        int push1 = Unsafe.Add(ref thresholds, 1);
        // The PUSH skip below steps up to 32 bytes past the end of the code, so the walk pins and moves
        // an unmanaged pointer: the same overshoot on a `ref byte` is a managed pointer outside its
        // object, which a relocating GC may adjust wrongly even though it is only ever compared - and
        // the differential tests run this scan on CoreCLR, whose GC does relocate.
        nuint analyzedUntil;
        fixed (byte* codeStart = code)
        {
            byte* position = codeStart + programCounter;
            byte* end = codeStart + code.Length;
            while (position < end)
            {
                // Sign extension folds everything above PUSH32 below JUMPDEST, so one signed comparison
                // covers the whole [JUMPDEST, PUSH32] window that the rebase-and-range-test needed two for.
                int op = (sbyte)*position;
                if (op >= jumpDest)
                {
                    if (op >= push1)
                    {
                        // One byte short: every path joins the single advance below, so nothing branches over it.
                        position += op - PUSH1 + 1;
                    }
                    else if (op == jumpDest)
                    {
                        nuint jumpDestination = (nuint)(position - codeStart);
                        if ((jumpDestination ^ flagsPosition) >> BitShiftPerInt64 != 0 && currentFlags != 0)
                        {
                            MarkJumpDestinations(bitmap, flagsPosition, currentFlags);
                            currentFlags = 0;
                        }

                        currentFlags |= 1L << (int)jumpDestination;
                        flagsPosition = jumpDestination;
                    }
                }

                position++;
            }
            analyzedUntil = (nuint)(position - codeStart);
        }

        if (currentFlags != 0)
        {
            MarkJumpDestinations(bitmap, flagsPosition, currentFlags);
        }
        return analyzedUntil;
    }

    /// <summary>Reports whether <paramref name="destination"/>, a JUMPDEST byte, starts an instruction, marking it if so.</summary>
    /// <param name="destination">The position of a JUMPDEST byte in <paramref name="code"/>.</param>
    /// <param name="bitmap">The code's incremental bitmap.</param>
    /// <param name="code">The code.</param>
    /// <param name="analyzedUntil">
    /// Where the contiguous scan from the start of the code stopped, an instruction start; advanced when the
    /// scan resumes from there.
    /// </param>
    /// <remarks>
    /// A byte is PUSH data only if a PUSH<i>k</i> opcode sits at most <i>k</i> bytes before it, so a position
    /// with no such byte among the 32 before it starts an instruction, whether or not those bytes are opcodes
    /// themselves: a destination proven that way is marked without scanning. Otherwise every position after the
    /// earliest such byte is within its reach too, so the search moves back to that byte, and scans from the
    /// first position it proves, since a scan from any instruction start marks the same jump destinations the
    /// scan from the start of the code would. Once the search comes within a PUSH's reach of the cursor, or runs
    /// out of look-backs or distance, the contiguous scan resumes instead, stopping at the first instruction
    /// boundary beyond the destination.
    /// </remarks>
    internal static bool AnalyzeJump(int destination, long[] bitmap, ReadOnlySpan<byte> code, ref nint analyzedUntil)
    {
        ref byte codeStart = ref MemoryMarshal.GetReference(code);
        nint cursor = analyzedUntil;
        // Within a PUSH's reach of the cursor, the contiguous scan is as cheap as any search.
        nint floor = Math.Max(cursor, destination - MaxLocalScan) + MaxImmediateLength;
        nint position = destination;
        nint lookBacks = MaxLookBacks;
        while (position > floor)
        {
            if (!TryFindReachingPush(ref Unsafe.Add(ref codeStart, position - MaxImmediateLength), out nint offset))
            {
                if (position != destination) return ScanFrom(position, destination, bitmap, code, ref analyzedUntil);

                MarkJumpDestinations(bitmap, (nuint)destination, 1L << destination);
                return true;
            }

            if (--lookBacks == 0) break;
            position += offset - MaxImmediateLength;
        }

        return destination < cursor
            ? IsJumpDestination(bitmap, destination)
            : ScanFrom(cursor, destination, bitmap, code, ref analyzedUntil);
    }

    /// <summary>Scans from the instruction start <paramref name="start"/> through <paramref name="destination"/> and reports whether it is marked.</summary>
    /// <remarks>Out of line, and reached by a tail call, so the look-back that decides most jumps does not pay for the scan's register saves.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool ScanFrom(nint start, int destination, long[] bitmap, ReadOnlySpan<byte> code, ref nint analyzedUntil)
    {
        nuint reached = ProcessJumpDestinationBitmap_Byte((nuint)start, bitmap, code[..(destination + 1)]);
        if (start == analyzedUntil) analyzedUntil = (nint)reached;
        return IsJumpDestination(bitmap, destination);
    }

    /// <summary>
    /// Reports whether a byte of the 32-byte <paramref name="window"/> may be a PUSH long enough to reach the
    /// position just past the window, returning in <paramref name="offset"/> one no later than the first that is.
    /// </summary>
    /// <remarks>
    /// The byte at offset <c>o</c> reaches that position when it lies in <c>[0x7f - o, 0x7f]</c>, that is when
    /// adding <c>o + 1</c> to it sets bit 7 while its own bit 7 is clear. Only a byte with bit 7 set can carry
    /// into the next one, and a carry can only set that byte's bit 7, never clear it: the result may name a byte
    /// that does not reach, which costs a scan, but never misses one that does.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryFindReachingPush(ref byte window, out nint offset)
    {
        ref ulong biases = ref MemoryMarshal.GetArrayDataReference(_reachBiases);
        ulong highBits = biases;
        ulong word0 = Unsafe.ReadUnaligned<ulong>(ref window);
        ulong word1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref window, sizeof(ulong)));
        ulong word2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref window, 2 * sizeof(ulong)));
        ulong word3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref window, 3 * sizeof(ulong)));
        ulong reach0 = (word0 + Unsafe.Add(ref biases, 1)) & ~word0;
        ulong reach1 = (word1 + Unsafe.Add(ref biases, 2)) & ~word1;
        ulong reach2 = (word2 + Unsafe.Add(ref biases, 3)) & ~word2;
        ulong reach3 = (word3 + Unsafe.Add(ref biases, 4)) & ~word3;
        offset = 0;
        if (((reach0 | reach1 | reach2 | reach3) & highBits) == 0) return false;

        ulong reaching = reach0 & highBits;
        if (reaching == 0) { offset = sizeof(ulong); reaching = reach1 & highBits; }
        if (reaching == 0) { offset = 2 * sizeof(ulong); reaching = reach2 & highBits; }
        if (reaching == 0) { offset = 3 * sizeof(ulong); reaching = reach3 & highBits; }
        offset += BitOperations.TrailingZeroCount(reaching) >> 3;
        return true;
    }
}
