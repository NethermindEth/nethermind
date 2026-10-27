// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm.CodeAnalysis;

public sealed partial class JumpDestinationAnalyzer
{
    // Guest execution is single-threaded; no cross-thread bitmap publication is needed.
    private long[]? _jumpDestinationBitmap = (codeInfo.CodeLength == 0 || skipAnalysis) ? _emptyJumpDestinationBitmap : null;

    /// <summary>The bitmap jumps are checked against when EIP-7979 adds no destination.</summary>
    /// <remarks>The incremental one, so code with no <c>CALLDEST</c> byte keeps its lazy analysis under EIP-7979.</remarks>
    private long[] PlainJumpBitmap => codeInfo.IncrementalJumpBitmap;

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

    /// <summary>How far before a destination a jump destination already marked may lie for the scan to start from it.</summary>
    /// <remarks>Scanning that far costs about as much as the look-backs it saves.</remarks>
    private const int MaxMarkedDistance = 64;

    /// <summary>The look-back's comparands, one row of <see cref="ReachRowLength"/> per offset of the window's end in its aligned word.</summary>
    /// <remarks>
    /// The look-back reads the five aligned words that hold the 32-byte window, as strict alignment expands an unaligned
    /// load into byte loads. Lane <c>L</c> of those 40 bytes is window byte <c>L - skew</c>, <c>skew</c> being the end's
    /// offset in its word; row <c>skew</c> holds each word's lane biases - the window byte's offset plus one, 0 outside the
    /// window - then the last word's high-bit mask, which keeps only the lanes before the window's end, then the high bit
    /// of each byte. An array for the same reason as <see cref="_byteScanThresholds"/>: materialising each 64-bit constant
    /// takes several instructions.
    /// </remarks>
    private static readonly ulong[] _reachBiases =
    [
        0x0807_0605_0403_0201UL, 0x100f_0e0d_0c0b_0a09UL, 0x1817_1615_1413_1211UL, 0x201f_1e1d_1c1b_1a19UL,
        0x0000_0000_0000_0000UL, 0x0000_0000_0000_0000UL, 0x8080_8080_8080_8080UL, 0,
        0x0706_0504_0302_0100UL, 0x0f0e_0d0c_0b0a_0908UL, 0x1716_1514_1312_1110UL, 0x1f1e_1d1c_1b1a_1918UL,
        0x0000_0000_0000_0020UL, 0x0000_0000_0000_0080UL, 0x8080_8080_8080_8080UL, 0,
        0x0605_0403_0201_0000UL, 0x0e0d_0c0b_0a09_0807UL, 0x1615_1413_1211_100fUL, 0x1e1d_1c1b_1a19_1817UL,
        0x0000_0000_0000_201fUL, 0x0000_0000_0000_8080UL, 0x8080_8080_8080_8080UL, 0,
        0x0504_0302_0100_0000UL, 0x0d0c_0b0a_0908_0706UL, 0x1514_1312_1110_0f0eUL, 0x1d1c_1b1a_1918_1716UL,
        0x0000_0000_0020_1f1eUL, 0x0000_0000_0080_8080UL, 0x8080_8080_8080_8080UL, 0,
        0x0403_0201_0000_0000UL, 0x0c0b_0a09_0807_0605UL, 0x1413_1211_100f_0e0dUL, 0x1c1b_1a19_1817_1615UL,
        0x0000_0000_201f_1e1dUL, 0x0000_0000_8080_8080UL, 0x8080_8080_8080_8080UL, 0,
        0x0302_0100_0000_0000UL, 0x0b0a_0908_0706_0504UL, 0x1312_1110_0f0e_0d0cUL, 0x1b1a_1918_1716_1514UL,
        0x0000_0020_1f1e_1d1cUL, 0x0000_0080_8080_8080UL, 0x8080_8080_8080_8080UL, 0,
        0x0201_0000_0000_0000UL, 0x0a09_0807_0605_0403UL, 0x1211_100f_0e0d_0c0bUL, 0x1a19_1817_1615_1413UL,
        0x0000_201f_1e1d_1c1bUL, 0x0000_8080_8080_8080UL, 0x8080_8080_8080_8080UL, 0,
        0x0100_0000_0000_0000UL, 0x0908_0706_0504_0302UL, 0x1110_0f0e_0d0c_0b0aUL, 0x1918_1716_1514_1312UL,
        0x0020_1f1e_1d1c_1b1aUL, 0x0080_8080_8080_8080UL, 0x8080_8080_8080_8080UL, 0,
    ];

    private const int ReachRowLength = 8;
    private const int ReachTailMask = 5;
    private const int ReachHighBits = 6;

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
        nint jumpDest = thresholds;
        nint push1 = Unsafe.Add(ref thresholds, 1);
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
                nint op = (sbyte)*position;
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
    /// <param name="code">The first byte of the code.</param>
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
    /// scan from the start of the code would; a jump destination already marked at most
    /// <see cref="MaxMarkedDistance"/> bytes before it is such a start, so the scan starts from the last one without
    /// a look-back. Once the search comes within a PUSH's reach of the cursor, or runs
    /// out of look-backs or distance, the contiguous scan resumes instead, stopping at the first instruction
    /// boundary beyond the destination.
    /// </remarks>
    // Inlined so the scanned-jump handler, which runs it for every scan, pays for no call or second frame.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool AnalyzeJump(nint destination, long[] bitmap, ref byte codeStart, ref nint analyzedUntil)
    {
        nint cursor = analyzedUntil;
        // A marked jump destination starts an instruction, so the scan may start there without any look-back.
        nint start = destination > cursor ? FindMarkedBelow(bitmap, destination, Math.Max(cursor, destination - MaxMarkedDistance)) : -1;
        if (start < 0)
        {
            // Within a PUSH's reach of the cursor, the contiguous scan is as cheap as any search.
            nint floor = Math.Max(cursor, destination - MaxLocalScan) + MaxImmediateLength;
            nint position = destination;
            nint lookBacks = MaxLookBacks;
            // Read once here: inside the search, ILC rematerialises the array's address on every look-back.
            ref ulong biases = ref MemoryMarshal.GetArrayDataReference(_reachBiases);
            while (position > floor)
            {
                if (!TryFindReachingPush(ref Unsafe.Add(ref codeStart, position), ref biases, out nint offset))
                {
                    start = position;
                    break;
                }

                if (--lookBacks == 0) break;
                position += offset - MaxImmediateLength;
            }

            if (start < 0)
            {
                if (destination < cursor) return IsJumpDestination(bitmap, (int)destination);
                start = cursor;
            }
        }

        return ScanTo(start, destination, bitmap, ref codeStart, ref analyzedUntil);
    }

    /// <summary>Reports whether the 32 bytes before <paramref name="destination"/> prove that it starts an instruction.</summary>
    /// <param name="destination">A position in the code.</param>
    /// <param name="code">The first byte of the code.</param>
    /// <param name="analyzedUntil">Where the contiguous scan from the start of the code stopped.</param>
    /// <remarks>
    /// The first look-back of <see cref="AnalyzeJump"/> on its own, which decides most destinations without a call. A
    /// false answer only means it could not decide: a PUSH may reach the destination, or the destination lies within a
    /// PUSH's reach of the cursor, where <see cref="AnalyzeJump"/> scans instead.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsProvenByLookBack(nint destination, ref byte code, nint analyzedUntil) =>
        destination > analyzedUntil + MaxImmediateLength && !MayHoldReachingPush(ref Unsafe.Add(ref code, destination));

    /// <summary>Reports whether a byte of the 32 bytes before <paramref name="end"/> may be a PUSH long enough to reach it.</summary>
    /// <remarks>
    /// The test of <see cref="TryFindReachingPush"/> without the offset, folded a word at a time, so that a handler taking
    /// it keeps two words live rather than eight and needs no callee-saved register. It reads up to 6 bytes before the
    /// window and 7 past its end, whose lanes the row zeroes or masks.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool MayHoldReachingPush(ref byte end)
    {
        nint skew = Skew(ref end);
        ref ulong row = ref Aligned(ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_reachBiases), skew * ReachRowLength));
        ref ulong word = ref Aligned(ref FirstWindowWord(ref end, skew));
        ulong reach = ReachingBytes(ref word, ref row, 0) | ReachingBytes(ref word, ref row, 1) |
            ReachingBytes(ref word, ref row, 2) | ReachingBytes(ref word, ref row, 3);
        return ((reach & Unsafe.Add(ref row, ReachHighBits)) |
            (ReachingBytes(ref word, ref row, 4) & Unsafe.Add(ref row, ReachTailMask))) != 0;
    }

    /// <summary>The offset of <paramref name="end"/> in its aligned word, which picks the look-back's row of biases.</summary>
    /// <remarks>
    /// A relocating GC moves objects by whole words, so the offset survives it, and the words are reached through
    /// managed references: the host's CoreCLR tests run this code as the guest does.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe nint Skew(ref byte end) => (nint)((nuint)Unsafe.AsPointer(ref end) & (sizeof(ulong) - 1));

    /// <summary>The first of the five aligned words holding the 32 bytes before <paramref name="end"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref ulong FirstWindowWord(ref byte end, nint skew) =>
        ref Unsafe.Add(ref Unsafe.As<byte, ulong>(ref Unsafe.Subtract(ref end, skew)), -(MaxImmediateLength / sizeof(ulong)));

    /// <summary><paramref name="word"/>, rounded down to the word boundary it is already on where <see cref="StrictAlignmentFlag"/> is on.</summary>
    /// <remarks>One <c>andi</c> lets the JIT prove aligned every load at a whole number of words from the result, and drop their alignment tests.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref ulong Aligned(ref ulong word) => ref StrictAlignmentFlag.IsActive
        ? ref Unsafe.As<byte, ulong>(ref Unsafe.AddByteOffset(ref Unsafe.NullRef<byte>(),
            Unsafe.ByteOffset(ref Unsafe.NullRef<byte>(), ref Unsafe.As<ulong, byte>(ref word)) & ~(nint)(sizeof(ulong) - 1)))
        : ref word;

    /// <summary>Returns the last position below <paramref name="destination"/> and at or above <paramref name="limit"/> that <paramref name="bitmap"/> marks, or -1.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint FindMarkedBelow(long[] bitmap, nint destination, nint limit)
    {
        ref long words = ref MemoryMarshal.GetArrayDataReference(bitmap);
        nint word = destination >> BitShiftPerInt64;
        // The positions of the destination's word below it; a shift counts modulo 64.
        ulong marks = (ulong)Unsafe.Add(ref words, word) & ((1UL << (int)destination) - 1);
        while (marks == 0)
        {
            if (--word < limit >> BitShiftPerInt64) return -1;
            marks = (ulong)Unsafe.Add(ref words, word);
        }

        nint marked = (word << BitShiftPerInt64) + 63 - BitOperations.LeadingZeroCount(marks);
        return marked >= limit ? marked : -1;
    }

    /// <summary>Scans from the instruction start <paramref name="start"/> up to the JUMPDEST at <paramref name="destination"/> and reports whether it starts an instruction, marking it if so.</summary>
    /// <remarks>
    /// Inlined into <see cref="AnalyzeJump"/>, which then calls nothing: a scan is a few dozen bytes, so the calls and
    /// register saves of a separate scan cost more than the scan itself. Each jump destination is marked as it is
    /// found, as most scans meet only one or two. The JUMPDEST at the destination bounds the scan: no run of other
    /// opcodes passes it, so only a PUSH, whose data may cover it, is tested against it.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe bool ScanTo(nint start, nint destination, long[] bitmap, ref byte code, ref nint analyzedUntil)
    {
        ref long bits = ref MemoryMarshal.GetArrayDataReference(bitmap);
        ref int thresholds = ref MemoryMarshal.GetArrayDataReference(_byteScanThresholds);
        nint jumpDest = thresholds;
        nint push1 = Unsafe.Add(ref thresholds, 1);
        nint reached;
        bool startsInstruction;
        // An unmanaged pointer for the PUSH skip's overshoot, as in ProcessJumpDestinationBitmap_Byte.
        fixed (byte* codeStart = &code)
        {
            byte* position = codeStart + start - 1;
            byte* end = codeStart + destination;
            while (true)
            {
                // The JUMPDEST at the destination ends the run at the latest.
                nint op;
                do
                {
                    position++;
                    op = (sbyte)*position;
                } while (op < jumpDest);

                if (op >= push1)
                {
                    position += op - (PUSH1 - 1);
                    // The PUSH's data covers the destination.
                    if (position >= end)
                    {
                        startsInstruction = false;
                        break;
                    }
                }
                else if (op == jumpDest)
                {
                    nint jumpDestination = (nint)(position - codeStart);
                    Unsafe.Add(ref bits, jumpDestination >> BitShiftPerInt64) |= 1L << (int)jumpDestination;
                    if (position == end)
                    {
                        startsInstruction = true;
                        break;
                    }
                }
            }

            reached = (nint)(position + 1 - codeStart);
        }

        if (start == analyzedUntil) analyzedUntil = reached;
        return startsInstruction;
    }

    /// <summary>
    /// Reports whether a byte of the 32 bytes before <paramref name="end"/> may be a PUSH long enough to reach it,
    /// returning in <paramref name="offset"/>, counted from 32 bytes before <paramref name="end"/>, one no later than
    /// the first that is.
    /// </summary>
    /// <remarks>
    /// The byte at offset <c>o</c> reaches that position when it lies in <c>[0x7f - o, 0x7f]</c>, that is when
    /// adding <c>o + 1</c> to it sets bit 7 while its own bit 7 is clear. Only a byte with bit 7 set can carry
    /// into the next one, and a carry can only set that byte's bit 7, never clear it: the result may name a byte
    /// that does not reach, which costs a scan, but never misses one that does. A lane outside the window has
    /// bias 0, so it is never named unless a carry reaches it; the last word's mask drops those past the end.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryFindReachingPush(ref byte end, ref ulong biases, out nint offset)
    {
        nint skew = Skew(ref end);
        ref ulong row = ref Aligned(ref Unsafe.Add(ref biases, skew * ReachRowLength));
        ref ulong word = ref Aligned(ref FirstWindowWord(ref end, skew));
        ulong highBits = Unsafe.Add(ref row, ReachHighBits);
        // A word at a time, stopping at the first that reaches, so the search holds one word rather than eight.
        offset = -skew;
        ulong reaching = ReachingBytes(ref word, ref row, 0) & highBits;
        if (reaching == 0) { offset += sizeof(ulong); reaching = ReachingBytes(ref word, ref row, 1) & highBits; }
        if (reaching == 0) { offset += sizeof(ulong); reaching = ReachingBytes(ref word, ref row, 2) & highBits; }
        if (reaching == 0) { offset += sizeof(ulong); reaching = ReachingBytes(ref word, ref row, 3) & highBits; }
        if (reaching == 0) { offset += sizeof(ulong); reaching = ReachingBytes(ref word, ref row, 4) & Unsafe.Add(ref row, ReachTailMask); }
        if (reaching == 0) return false;

        offset += BitOperations.TrailingZeroCount(reaching) >> 3;
        return true;
    }

    /// <summary>The bytes of aligned window word <paramref name="index"/> whose high bit adding their bias in <paramref name="row"/> sets.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReachingBytes(ref ulong word, ref ulong row, int index)
    {
        ulong bytes = Unsafe.Add(ref word, index);
        return (bytes + Unsafe.Add(ref row, index)) & ~bytes;
    }
}
