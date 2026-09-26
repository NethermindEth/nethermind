// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core.Extensions;

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]
[assembly: InternalsVisibleTo("Nethermind.Evm.ZkEvm.Test")]
[assembly: InternalsVisibleTo("Nethermind.Benchmark")]

namespace Nethermind.Evm.CodeAnalysis;

public sealed partial class JumpDestinationAnalyzer(CodeInfo codeInfo, bool skipAnalysis = false)
{
    private const int PUSH1 = (int)Instruction.PUSH1;
    private const int PUSHx = PUSH1 - 1;
    private const int JUMPDEST = (int)Instruction.JUMPDEST;
    private const int PUSH32 = (int)Instruction.PUSH32;
    private const int BitShiftPerInt64 = 6;

    private static readonly long[] _emptyJumpDestinationBitmap = new long[1];
    /// <summary>A bitmap with no valid jump destination, for code that has no analyzer.</summary>
    internal static long[] EmptyBitmap => _emptyJumpDestinationBitmap;
    public ReadOnlyMemory<byte> MachineCode => codeInfo.Code;

    /// <summary>The jump-destination bitmap, built on first use; one bit per code byte.</summary>
    internal long[] JumpDestinationBitmap
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _jumpDestinationBitmap ??= CreateJumpDestinationBitmap();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ValidateJump(int destination)
    {
        long[] bitmap = JumpDestinationBitmap;

        // Cast to uint to change negative numbers to very int high numbers
        // Then do length check, this both reduces check by 1 and eliminates the bounds
        // check from accessing the span.
        return (uint)destination < (uint)MachineCode.Length && IsJumpDestination(bitmap, destination);
    }

    /// <summary>
    /// Used for conversion between different representations of bit array.
    /// Returns (n + (64 - 1)) / 64, rearranged to avoid arithmetic overflow.
    /// For example, in the bit to int case, the straightforward calc would
    /// be (n + 63) / 64, but that would cause overflow. So instead it's
    /// rearranged to ((n - 1) / 64) + 1.
    /// Due to sign extension, we don't need to special case for n == 0, if we use
    /// bitwise operations (since ((n - 1) >> 6) + 1 = 0).
    /// This doesn't hold true for ((n - 1) / 64) + 1, which equals 1.
    ///
    /// Usage:
    /// GetInt32ArrayLengthFromBitLength(77): returns how many ints must be
    /// allocated to store 77 bits.
    /// </summary>
    /// <param name="n"></param>
    /// <returns>how many ints are required to store n bytes</returns>
    private static int GetInt64ArrayLengthFromBitLength(int n) =>
        (n - 1 + (1 << BitShiftPerInt64)) >>> BitShiftPerInt64;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private long[] CreateJumpDestinationBitmap()
    {
        Metrics.IncrementContractsAnalysed();
        ReadOnlySpan<byte> code = MachineCode.Span;

        // If code is empty or starts with STOP, then we don't need to analyze
        if ((uint)code.Length < (uint)1 || code[0] == (byte)Instruction.STOP) return _emptyJumpDestinationBitmap;

        long[] bitmap = CreateBitmap(code.Length);

        return Avx512Vbmi.IsSupported && Vector512.IsHardwareAccelerated && code.Length >= Vector512<sbyte>.Count ? PopulateJumpDestinationBitmap_Vector512(bitmap, code) :
            Avx2.IsSupported && code.Length >= Vector256<sbyte>.Count ? PopulateJumpDestinationBitmap_Vector256(bitmap, code) :
            Ssse3.IsSupported && code.Length >= Vector128<sbyte>.Count ? PopulateJumpDestinationBitmap_Ssse3(bitmap, code) :
            AdvSimd.Arm64.IsSupported && code.Length >= Vector128<sbyte>.Count ? PopulateJumpDestinationBitmap_AdvSimd(bitmap, code) :
            PopulateJumpDestinationBitmap_Scalar(bitmap, code);
    }

    internal static long[] CreateBitmap(int codeLength)
        => new long[GetInt64ArrayLengthFromBitLength(codeLength)];

    // Keeps 16-byte positions in 0x70..0x7f, so an x86 byte shuffle, which zeroes a lane whose index has bit 7 set,
    // reads an exit (0x80 and above) as zero.
    private const byte ShufflePositionBias = 0x70;
    private const ulong EvenLanes = 0x5555_5555_5555_5555UL;

    /// <summary>Returns the lanes that hold the data byte of a real PUSH1.</summary>
    /// <param name="push1s">Lanes holding 0x60 that can start an instruction; lanes carried in as PUSH data are clear.</param>
    /// <remarks>
    /// In a run of 0x60 bytes, every second byte counted from the start of the run is data: the even lanes when the run
    /// starts on an odd lane. Adding its start clears such a run; each run left starts on an even lane, and XOR-ing it,
    /// shifted by one, into the even-lane mask moves its data to the odd lanes.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Push1DataLanes(ulong push1s)
    {
        ulong follows = push1s << 1;
        ulong oddRunStarts = push1s & ~EvenLanes & ~follows;
        return (EvenLanes ^ ((oddRunStarts + push1s) << 1)) & follows;
    }

    /// <summary>Returns 1 when the last lane is a real PUSH1, so the byte after <paramref name="push1s"/> is its data.</summary>
    /// <remarks>The last lane is a real PUSH1 exactly when the run of 0x60 bytes that ends there has odd length.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Push1CarryOut(ulong push1s) => (ulong)Bytes.LeadingZeroBits(~push1s) & 1;

    /// <remarks>
    /// Each lane of a 64-byte block starts as its offset plus the length of the instruction that would start there.
    /// Six self-compositions of that map give every lane its exit from the block, which carries the entry offset to
    /// the next block. Walking back down the compositions finds, for each lane, the last instruction start at or before
    /// it; the lane is an instruction start when that is the lane itself. Blocks without a 0x5b skip the walk back,
    /// and runs of pairs without a PUSH2..PUSH32 byte skip the doubling: there the only data lanes, apart from those
    /// carried in, are PUSH1 immediates, found by carry arithmetic. Requires AVX-512 VBMI.
    /// </remarks>
    internal static long[] PopulateJumpDestinationBitmap_Vector512(long[] bitmap, ReadOnlySpan<byte> code)
    {
        ref byte codeRef = ref MemoryMarshal.GetReference(code);
        Vector512<byte> lanes = Vector512<byte>.Indices;
        Vector512<byte> blockSize = Vector512.Create((byte)Vector512<byte>.Count);
        // Upper table of the two-table permute: an offset past the block maps to itself.
        Vector512<byte> exits = lanes + blockSize;
        // Signed max turns every byte that is not a PUSH, 0x80..0xff included, into PUSHx, so the sum is lane + length.
        Vector512<byte> endOffsets = lanes - Vector512.Create((byte)(PUSHx - 1));
        Vector512<sbyte> jumpDest = Vector512.Create((sbyte)JUMPDEST);
        Vector512<sbyte> pushx = Vector512.Create((sbyte)PUSHx);
        Vector512<sbyte> push1 = Vector512.Create((sbyte)PUSH1);
        int blocks = code.Length >> BitShiftPerInt64;
        int block = 0;
        // Offset of the first instruction in the next block to analyze, the same in every lane; at most 32.
        Vector512<byte> entry = Vector512<byte>.Zero;
        while (block + 1 < blocks)
        {
            Vector512<sbyte> opsA = Vector512.LoadUnsafe(ref codeRef, (nuint)block << BitShiftPerInt64).AsSByte();
            Vector512<sbyte> opsB = Vector512.LoadUnsafe(ref codeRef, ((nuint)block + 1) << BitShiftPerInt64).AsSByte();
            Vector512<sbyte> max = Vector512.Max(opsA, opsB);
            // The masks go through a general-purpose register: a kortest here measured 6% slower on Zen 5.
            if (Vector512.GreaterThan(max, push1).ExtractMostSignificantBits() == 0)
            {
                ulong carried = Vector512.LessThan(lanes, entry).ExtractMostSignificantBits();
                if (Vector512.Equals(max, push1).ExtractMostSignificantBits() == 0)
                {
                    // Every instruction here is one byte, and the entry is below 64, so only block A of the first pair
                    // has carried-in data; later pairs in the loop only need the check that they hold no PUSH.
                    while (true)
                    {
                        bitmap[block] = (long)(Vector512.Equals(opsA, jumpDest).ExtractMostSignificantBits() & ~carried);
                        bitmap[block + 1] = (long)Vector512.Equals(opsB, jumpDest).ExtractMostSignificantBits();
                        block += 2;
                        if (block + 1 >= blocks) break;
                        opsA = Vector512.LoadUnsafe(ref codeRef, (nuint)block << BitShiftPerInt64).AsSByte();
                        opsB = Vector512.LoadUnsafe(ref codeRef, ((nuint)block + 1) << BitShiftPerInt64).AsSByte();
                        if (Vector512.GreaterThan(Vector512.Max(opsA, opsB), pushx).ExtractMostSignificantBits() != 0) break;
                        carried = 0;
                    }

                    entry = Vector512<byte>.Zero;
                    continue;
                }

                // Out of line, so the doubling loop keeps its code layout.
                block = MarkPush1Run(bitmap, code, block, carried, out carried);
                entry = carried == 0 ? Vector512<byte>.Zero : Vector512<byte>.One;
                continue;
            }

            Vector512<byte> a0 = Vector512.Max(opsA, pushx).AsByte() + endOffsets;
            Vector512<byte> b0 = Vector512.Max(opsB, pushx).AsByte() + endOffsets;
            Vector512<byte> a1 = Compose64(a0, exits);
            Vector512<byte> b1 = Compose64(b0, exits);
            Vector512<byte> a2 = Compose64(a1, exits);
            Vector512<byte> b2 = Compose64(b1, exits);
            Vector512<byte> a3 = Compose64(a2, exits);
            Vector512<byte> b3 = Compose64(b2, exits);
            Vector512<byte> a4 = Compose64(a3, exits);
            Vector512<byte> b4 = Compose64(b3, exits);
            Vector512<byte> a5 = Compose64(a4, exits);
            Vector512<byte> b5 = Compose64(b4, exits);
            Vector512<byte> a6 = Compose64(a5, exits);
            Vector512<byte> b6 = Compose64(b5, exits);
            // Keep each block's own entry: lifting a block from the next block's entry loses its destinations.
            Vector512<byte> entryA = entry;
            Vector512<byte> entryB = Avx512Vbmi.PermuteVar64x8(a6, entryA) - blockSize;
            entry = Avx512Vbmi.PermuteVar64x8(b6, entryB) - blockSize;
            ulong jumpDestsA = Vector512.Equals(opsA, jumpDest).ExtractMostSignificantBits();
            ulong jumpDestsB = Vector512.Equals(opsB, jumpDest).ExtractMostSignificantBits();
            if (jumpDestsA != 0)
            {
                Vector512<byte> position = Descend64(a5, entryA, lanes);
                position = Descend64(a4, position, lanes);
                position = Descend64(a3, position, lanes);
                position = Descend64(a2, position, lanes);
                position = Descend64(a1, position, lanes);
                position = Descend64(a0, position, lanes);
                bitmap[block] = (long)(jumpDestsA & Vector512.Equals(position, lanes).ExtractMostSignificantBits());
            }

            if (jumpDestsB != 0)
            {
                Vector512<byte> position = Descend64(b5, entryB, lanes);
                position = Descend64(b4, position, lanes);
                position = Descend64(b3, position, lanes);
                position = Descend64(b2, position, lanes);
                position = Descend64(b1, position, lanes);
                position = Descend64(b0, position, lanes);
                bitmap[block + 1] = (long)(jumpDestsB & Vector512.Equals(position, lanes).ExtractMostSignificantBits());
            }

            block += 2;
        }

        if (block < blocks)
        {
            Vector512<sbyte> ops = Vector512.LoadUnsafe(ref codeRef, (nuint)block << BitShiftPerInt64).AsSByte();
            Vector512<byte> n0 = Vector512.Max(ops, pushx).AsByte() + endOffsets;
            Vector512<byte> n1 = Compose64(n0, exits);
            Vector512<byte> n2 = Compose64(n1, exits);
            Vector512<byte> n3 = Compose64(n2, exits);
            Vector512<byte> n4 = Compose64(n3, exits);
            Vector512<byte> n5 = Compose64(n4, exits);
            Vector512<byte> n6 = Compose64(n5, exits);
            Vector512<byte> blockEntry = entry;
            entry = Avx512Vbmi.PermuteVar64x8(n6, blockEntry) - blockSize;
            ulong jumpDests = Vector512.Equals(ops, jumpDest).ExtractMostSignificantBits();
            if (jumpDests != 0)
            {
                Vector512<byte> position = Descend64(n5, blockEntry, lanes);
                position = Descend64(n4, position, lanes);
                position = Descend64(n3, position, lanes);
                position = Descend64(n2, position, lanes);
                position = Descend64(n1, position, lanes);
                position = Descend64(n0, position, lanes);
                bitmap[block] = (long)(jumpDests & Vector512.Equals(position, lanes).ExtractMostSignificantBits());
            }
        }

        int analyzed = blocks << BitShiftPerInt64;
        int pending = entry.ToScalar();
        if (analyzed + pending < code.Length)
        {
            ProcessJumpDestinationBitmap_Byte((nuint)pending, bitmap.AsSpan(blocks), code.Slice(analyzed));
        }

        return bitmap;
    }

    /// <summary>
    /// Marks the pairs of 64-byte blocks from <paramref name="block"/> on while they hold no PUSH2..PUSH32 byte, and
    /// returns the first block after them. The caller has checked the first pair, which is always consumed.
    /// </summary>
    /// <param name="carried">Lanes of the first block carried in as PUSH data.</param>
    /// <param name="carryOut">1 when the byte after the run is the data of a PUSH1 in its last lane.</param>
    /// <remarks>
    /// Apart from <paramref name="carried"/>, the only data lanes are PUSH1 immediates, found by carry arithmetic, and the
    /// only state passed from one pair to the next is whether its lane 0 is data.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int MarkPush1Run(long[] bitmap, ReadOnlySpan<byte> code, int block, ulong carried, out ulong carryOut)
    {
        ref byte codeRef = ref MemoryMarshal.GetReference(code);
        Vector512<sbyte> jumpDest = Vector512.Create((sbyte)JUMPDEST);
        Vector512<sbyte> push1 = Vector512.Create((sbyte)PUSH1);
        int blocks = code.Length >> BitShiftPerInt64;
        Vector512<sbyte> opsA = Vector512.LoadUnsafe(ref codeRef, (nuint)block << BitShiftPerInt64).AsSByte();
        Vector512<sbyte> opsB = Vector512.LoadUnsafe(ref codeRef, ((nuint)block + 1) << BitShiftPerInt64).AsSByte();
        Vector512<sbyte> max = Vector512.Max(opsA, opsB);
        while (true)
        {
            ulong jumpDestsA = Vector512.Equals(opsA, jumpDest).ExtractMostSignificantBits();
            ulong jumpDestsB = Vector512.Equals(opsB, jumpDest).ExtractMostSignificantBits();
            if (Vector512.Equals(max, push1).ExtractMostSignificantBits() == 0)
            {
                bitmap[block] = (long)(jumpDestsA & ~carried);
                bitmap[block + 1] = (long)jumpDestsB;
                carried = 0;
            }
            else
            {
                ulong push1sA = Vector512.Equals(opsA, push1).ExtractMostSignificantBits() & ~carried;
                ulong push1sB = Vector512.Equals(opsB, push1).ExtractMostSignificantBits();
                ulong carriedB = Push1CarryOut(push1sA);
                if ((jumpDestsA | jumpDestsB) != 0)
                {
                    bitmap[block] = (long)(jumpDestsA & ~(carried | Push1DataLanes(push1sA)));
                    bitmap[block + 1] = (long)(jumpDestsB & ~(carriedB | Push1DataLanes(push1sB & ~carriedB)));
                }

                // A run of 0x60 bytes that fills block B continues from block A; only its length parity counts.
                carried = Push1CarryOut(push1sB == ulong.MaxValue ? push1sA : push1sB);
            }

            block += 2;
            if (block + 1 >= blocks) break;
            opsA = Vector512.LoadUnsafe(ref codeRef, (nuint)block << BitShiftPerInt64).AsSByte();
            opsB = Vector512.LoadUnsafe(ref codeRef, ((nuint)block + 1) << BitShiftPerInt64).AsSByte();
            max = Vector512.Max(opsA, opsB);
            if (Vector512.GreaterThan(max, push1).ExtractMostSignificantBits() != 0) break;
        }

        carryOut = carried;
        return block;
    }

    /// <summary>Applies the successor map to itself; offsets past the block map to themselves.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<byte> Compose64(Vector512<byte> successors, Vector512<byte> exits)
        => Avx512Vbmi.PermuteVar64x8x2(successors, successors, exits);

    /// <summary>Advances each position along the successor map unless that passes the position's own lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<byte> Descend64(Vector512<byte> successors, Vector512<byte> positions, Vector512<byte> lanes)
    {
        Vector512<byte> next = Avx512Vbmi.PermuteVar64x8(successors, positions);
        return Vector512.ConditionalSelect(Vector512.LessThanOrEqual(next, lanes), next, positions);
    }

    /// <remarks>
    /// Two independent 16-byte blocks per register, with the same successor doubling as the 64-byte kernel. Positions
    /// are biased to 0x70..0x7f: vpshufb indexes by the low nibble and returns zero for an index with bit 7 set, so an
    /// exit (0x80 and above) reads as zero, and an unsigned max restores it because paths only move forward. Requires AVX2.
    /// </remarks>
    internal static long[] PopulateJumpDestinationBitmap_Vector256(long[] bitmap, ReadOnlySpan<byte> code)
    {
        const int PairLength = 2 * 16;

        ref byte codeRef = ref MemoryMarshal.GetReference(code);
        Vector256<byte> lanes = (Vector256<byte>.Indices & Vector256.Create((byte)15)) + Vector256.Create(ShufflePositionBias);
        Vector256<byte> endOffsets = lanes - Vector256.Create((byte)(PUSHx - 1));
        Vector256<byte> signBit = Vector256.Create((byte)0x80);
        Vector256<sbyte> lanesFlipped = (lanes ^ signBit).AsSByte();
        int pairs = code.Length / PairLength;
        // ShufflePositionBias plus the offset of the first instruction in the next 16-byte block to analyze, in every
        // lane; the offset is at most 32, and above 15 that block is all PUSH data.
        Vector128<byte> entry = Vector128.Create(ShufflePositionBias);
        int pair = 0;
        for (; pair + 1 < pairs; pair += 2)
        {
            ulong low = JumpDestinations16x2(Vector256.LoadUnsafe(ref codeRef, (nuint)(pair * PairLength)).AsSByte(), ref entry, lanes, endOffsets, lanesFlipped, signBit);
            ulong high = JumpDestinations16x2(Vector256.LoadUnsafe(ref codeRef, (nuint)((pair + 1) * PairLength)).AsSByte(), ref entry, lanes, endOffsets, lanesFlipped, signBit);
            bitmap[pair >> 1] = (long)(low | (high << PairLength));
        }

        if (pair < pairs)
        {
            bitmap[pair >> 1] = JumpDestinations16x2(Vector256.LoadUnsafe(ref codeRef, (nuint)(pair * PairLength)).AsSByte(), ref entry, lanes, endOffsets, lanesFlipped, signBit);
        }

        // The byte scan numbers bits from the start of its bitmap word, so it starts at the word holding the first byte left.
        int analyzed = pairs * PairLength;
        int wordStart = analyzed & ~63;
        int pending = analyzed - wordStart + entry.ToScalar() - ShufflePositionBias;
        if (wordStart + pending < code.Length)
        {
            ProcessJumpDestinationBitmap_Byte((nuint)pending, bitmap.AsSpan(wordStart >> BitShiftPerInt64), code.Slice(wordStart));
        }

        return bitmap;
    }

    /// <summary>Returns the JUMPDEST lanes of 32 bytes that start an instruction, and moves <paramref name="entry"/> past them.</summary>
    /// <param name="entry">ShufflePositionBias plus the offset from these 32 bytes to their first instruction, at most 32, in every lane.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint JumpDestinations16x2(Vector256<sbyte> ops, ref Vector128<byte> entry,
        Vector256<byte> lanes, Vector256<byte> endOffsets, Vector256<sbyte> lanesFlipped, Vector256<byte> signBit)
    {
        const int BlockLength = 16;
        Vector256<sbyte> pushx = Vector256.Create((sbyte)PUSHx);
        uint jumpDests = Vector256.Equals(ops, Vector256.Create((sbyte)JUMPDEST)).ExtractMostSignificantBits();
        if (!Vector256.GreaterThanAny(ops, pushx))
        {
            // The carried offset is at most 32, so a 64-bit shift pair clears every lane carried in as PUSH data.
            int carried = entry.ToScalar() - ShufflePositionBias;
            entry = Vector128.Create(ShufflePositionBias);
            return (uint)((ulong)jumpDests >> carried << carried);
        }

        Vector256<byte> n0 = Vector256.Max(ops, pushx).AsByte() + endOffsets;
        Vector256<byte> n1 = Compose16x2(n0);
        Vector256<byte> n2 = Compose16x2(n1);
        Vector256<byte> n3 = Compose16x2(n2);
        // Every lane leaves its block within 16 steps, so each value is at least 0x80 before the subtraction.
        Vector256<byte> nextEntries = Compose16x2(n3) - Vector256.Create((byte)BlockLength);
        Vector128<byte> blockSize = Vector128.Create((byte)BlockLength);
        // An entry past its block reads as zero, so the max moves it one block on; otherwise the lookup is larger.
        Vector128<byte> entryA = entry;
        Vector128<byte> entryB = Vector128.Max(Ssse3.Shuffle(nextEntries.GetLower(), entryA), entryA - blockSize);
        entry = Vector128.Max(Ssse3.Shuffle(nextEntries.GetUpper(), entryB), entryB - blockSize);
        if (jumpDests == 0) return 0;

        Vector256<byte> entries = Vector256.Create(entryA, entryB);
        Vector256<byte> position = Descend16x2(n3, entries, lanesFlipped, signBit);
        position = Descend16x2(n2, position, lanesFlipped, signBit);
        position = Descend16x2(n1, position, lanesFlipped, signBit);
        // After the two-instruction level, the last start at or before a lane is the position or its successor, so the
        // lane starts an instruction exactly when it equals one of them.
        Vector256<byte> starts = Vector256.Equals(position, lanes) | Vector256.Equals(Avx2.Shuffle(n0, position), lanes);
        // An entry past its block (bit 7 set) means the whole block is PUSH data.
        return starts.ExtractMostSignificantBits() & jumpDests & ~entries.ExtractMostSignificantBits();
    }

    /// <summary>Applies the successor map of each 16-byte half to itself; exits map to themselves.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> Compose16x2(Vector256<byte> successors)
        => Vector256.Max(Avx2.Shuffle(successors, successors), successors);

    /// <summary>Advances each position along the successor map unless that passes the position's own lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> Descend16x2(Vector256<byte> successors, Vector256<byte> positions, Vector256<sbyte> lanesFlipped, Vector256<byte> signBit)
    {
        Vector256<byte> next = Avx2.Shuffle(successors, positions);
        // Unsigned next > lane, as a signed compare with both sign bits flipped. Exits always compare greater, so they
        // are never taken.
        Vector256<byte> passes = Vector256.GreaterThan((next ^ signBit).AsSByte(), lanesFlipped).AsByte();
        return Vector256.ConditionalSelect(passes, positions, next);
    }

    /// <remarks>
    /// Four 16-byte blocks per 64 bytes, with the same successor doubling, each finished before the next so the live
    /// vectors fit in the 16 SSE registers. Positions are biased to 0x70..0x7f so pshufb reads an exit as zero, and an
    /// unsigned max restores it. 64 bytes without a PUSH byte skip the doubling, and 64 bytes without a 0x5b skip the
    /// walk back. Requires SSSE3.
    /// </remarks>
    internal static long[] PopulateJumpDestinationBitmap_Ssse3(long[] bitmap, ReadOnlySpan<byte> code)
    {
        ref byte codeRef = ref MemoryMarshal.GetReference(code);
        Vector128<byte> lanes = Vector128<byte>.Indices + Vector128.Create(ShufflePositionBias);
        Vector128<byte> endOffsets = lanes - Vector128.Create((byte)(PUSHx - 1));
        Vector128<sbyte> jumpDest = Vector128.Create((sbyte)JUMPDEST);
        Vector128<sbyte> pushx = Vector128.Create((sbyte)PUSHx);
        // ShufflePositionBias plus the offset of the first instruction in the next 16-byte block to analyze, in every
        // lane; the offset is at most 32.
        Vector128<byte> entry = Vector128.Create(ShufflePositionBias);
        int blocks = code.Length >> BitShiftPerInt64;
        for (int block = 0; block < blocks; block++)
        {
            ref byte blockRef = ref Unsafe.Add(ref codeRef, block << BitShiftPerInt64);
            Vector128<sbyte> ops0 = Vector128.LoadUnsafe(ref blockRef).AsSByte();
            Vector128<sbyte> ops1 = Vector128.LoadUnsafe(ref blockRef, 16).AsSByte();
            Vector128<sbyte> ops2 = Vector128.LoadUnsafe(ref blockRef, 32).AsSByte();
            Vector128<sbyte> ops3 = Vector128.LoadUnsafe(ref blockRef, 48).AsSByte();
            ulong jumpDests = Vector128.Equals(ops0, jumpDest).ExtractMostSignificantBits()
                | ((ulong)Vector128.Equals(ops1, jumpDest).ExtractMostSignificantBits() << 16)
                | ((ulong)Vector128.Equals(ops2, jumpDest).ExtractMostSignificantBits() << 32)
                | ((ulong)Vector128.Equals(ops3, jumpDest).ExtractMostSignificantBits() << 48);
            if (!Vector128.GreaterThanAny(Vector128.Max(Vector128.Max(ops0, ops1), Vector128.Max(ops2, ops3)), pushx))
            {
                if (jumpDests != 0)
                {
                    // The carried offset is at most 32, so a 64-bit shift pair clears every lane carried in as PUSH data.
                    int carried = entry.ToScalar() - ShufflePositionBias;
                    bitmap[block] = (long)(jumpDests >> carried << carried);
                }

                entry = Vector128.Create(ShufflePositionBias);
                continue;
            }

            if (jumpDests == 0)
            {
                entry = NextEntry16(ops0, entry, endOffsets);
                entry = NextEntry16(ops1, entry, endOffsets);
                entry = NextEntry16(ops2, entry, endOffsets);
                entry = NextEntry16(ops3, entry, endOffsets);
                continue;
            }

            ulong starts = JumpDestinations16(ops0, ref entry, endOffsets, lanes)
                | ((ulong)JumpDestinations16(ops1, ref entry, endOffsets, lanes) << 16)
                | ((ulong)JumpDestinations16(ops2, ref entry, endOffsets, lanes) << 32)
                | ((ulong)JumpDestinations16(ops3, ref entry, endOffsets, lanes) << 48);
            bitmap[block] = (long)(starts & jumpDests);
        }

        int analyzed = blocks << BitShiftPerInt64;
        int pending = entry.ToScalar() - ShufflePositionBias;
        if (analyzed + pending < code.Length)
        {
            ProcessJumpDestinationBitmap_Byte((nuint)pending, bitmap.AsSpan(blocks), code.Slice(analyzed));
        }

        return bitmap;
    }

    /// <summary>Returns the entry into the 16-byte block after <paramref name="ops"/>, given the entry into it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> NextEntry16(Vector128<sbyte> ops, Vector128<byte> entry, Vector128<byte> endOffsets)
    {
        Successors16(ops, endOffsets, out _, out _, out _, out _, out Vector128<byte> exits);
        return NextEntry16(exits, entry);
    }

    /// <remarks>
    /// Every lane leaves its block within 16 steps, so each exit is at least 0x80 before the subtraction. An entry past
    /// its block reads as zero, so the max moves it one block on; otherwise the lookup is larger.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> NextEntry16(Vector128<byte> exits, Vector128<byte> entry)
    {
        Vector128<byte> blockSize = Vector128.Create((byte)16);
        return Vector128.Max(Ssse3.Shuffle(exits - blockSize, entry), entry - blockSize);
    }

    /// <summary>Returns the JUMPDEST candidates of 16 bytes that start an instruction, and moves <paramref name="entry"/> past them.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint JumpDestinations16(Vector128<sbyte> ops, ref Vector128<byte> entry, Vector128<byte> endOffsets, Vector128<byte> lanes)
    {
        Successors16(ops, endOffsets, out Vector128<byte> n0, out Vector128<byte> n1, out Vector128<byte> n2, out Vector128<byte> n3, out Vector128<byte> exits);
        Vector128<byte> blockEntry = entry;
        entry = NextEntry16(exits, blockEntry);
        Vector128<byte> position = Descend16(n3, blockEntry, lanes);
        position = Descend16(n2, position, lanes);
        position = Descend16(n1, position, lanes);
        // After the two-instruction level, the last start at or before a lane is the position or its successor, so the
        // lane starts an instruction exactly when it equals one of them.
        Vector128<byte> starts = Vector128.Equals(position, lanes) | Vector128.Equals(Ssse3.Shuffle(n0, position), lanes);
        // pshufb reads an entry past the block (bit 7 set) as zero, which can land on a lane.
        return starts.ExtractMostSignificantBits() & ~blockEntry.ExtractMostSignificantBits();
    }

    /// <remarks>
    /// Four independent 16-byte blocks per 64 bytes, with the same successor doubling. TBX keeps the index for an
    /// out-of-range lane, so an exit maps to itself. 64 bytes without a PUSH byte skip the doubling. Requires AdvSimd.
    /// </remarks>
    internal static long[] PopulateJumpDestinationBitmap_AdvSimd(long[] bitmap, ReadOnlySpan<byte> code)
    {
        ref byte codeRef = ref MemoryMarshal.GetReference(code);
        Vector128<byte> lanes = Vector128<byte>.Indices;
        Vector128<byte> endOffsets = lanes - Vector128.Create((byte)(PUSHx - 1));
        Vector128<byte> blockSize = Vector128.Create((byte)16);
        Vector128<sbyte> jumpDest = Vector128.Create((sbyte)JUMPDEST);
        Vector128<sbyte> pushx = Vector128.Create((sbyte)PUSHx);
        // Offset of the first instruction in the next 16-byte block to analyze, in every lane; at most 32.
        Vector128<byte> entry = Vector128<byte>.Zero;
        int blocks = code.Length >> BitShiftPerInt64;
        for (int block = 0; block < blocks; block++)
        {
            ref byte blockRef = ref Unsafe.Add(ref codeRef, block << BitShiftPerInt64);
            Vector128<sbyte> ops0 = Vector128.LoadUnsafe(ref blockRef).AsSByte();
            Vector128<sbyte> ops1 = Vector128.LoadUnsafe(ref blockRef, 16).AsSByte();
            Vector128<sbyte> ops2 = Vector128.LoadUnsafe(ref blockRef, 32).AsSByte();
            Vector128<sbyte> ops3 = Vector128.LoadUnsafe(ref blockRef, 48).AsSByte();
            Vector128<byte> jumpDests0 = Vector128.Equals(ops0, jumpDest).AsByte();
            Vector128<byte> jumpDests1 = Vector128.Equals(ops1, jumpDest).AsByte();
            Vector128<byte> jumpDests2 = Vector128.Equals(ops2, jumpDest).AsByte();
            Vector128<byte> jumpDests3 = Vector128.Equals(ops3, jumpDest).AsByte();
            bool hasJumpDest = ((jumpDests0 | jumpDests1) | (jumpDests2 | jumpDests3)) != Vector128<byte>.Zero;
            if (!Vector128.GreaterThanAny(Vector128.Max(Vector128.Max(ops0, ops1), Vector128.Max(ops2, ops3)), pushx))
            {
                if (hasJumpDest)
                {
                    int carried = entry.ToScalar();
                    bitmap[block] = (long)(ToBitmapWord(jumpDests0, jumpDests1, jumpDests2, jumpDests3) & (ulong.MaxValue << carried));
                }

                entry = Vector128<byte>.Zero;
                continue;
            }

            Successors16(ops0, endOffsets, out Vector128<byte> a0, out Vector128<byte> a1, out Vector128<byte> a2, out Vector128<byte> a3, out Vector128<byte> a4);
            Successors16(ops1, endOffsets, out Vector128<byte> b0, out Vector128<byte> b1, out Vector128<byte> b2, out Vector128<byte> b3, out Vector128<byte> b4);
            Successors16(ops2, endOffsets, out Vector128<byte> c0, out Vector128<byte> c1, out Vector128<byte> c2, out Vector128<byte> c3, out Vector128<byte> c4);
            Successors16(ops3, endOffsets, out Vector128<byte> d0, out Vector128<byte> d1, out Vector128<byte> d2, out Vector128<byte> d3, out Vector128<byte> d4);
            Vector128<byte> entry0 = entry;
            Vector128<byte> entry1 = Advance16(a4, entry0) - blockSize;
            Vector128<byte> entry2 = Advance16(b4, entry1) - blockSize;
            Vector128<byte> entry3 = Advance16(c4, entry2) - blockSize;
            entry = Advance16(d4, entry3) - blockSize;
            if (!hasJumpDest) continue;

            Vector128<byte> starts0 = InstructionStarts16(a0, a1, a2, a3, entry0, lanes) & jumpDests0;
            Vector128<byte> starts1 = InstructionStarts16(b0, b1, b2, b3, entry1, lanes) & jumpDests1;
            Vector128<byte> starts2 = InstructionStarts16(c0, c1, c2, c3, entry2, lanes) & jumpDests2;
            Vector128<byte> starts3 = InstructionStarts16(d0, d1, d2, d3, entry3, lanes) & jumpDests3;
            bitmap[block] = (long)ToBitmapWord(starts0, starts1, starts2, starts3);
        }

        int analyzed = blocks << BitShiftPerInt64;
        int pending = entry.ToScalar();
        if (analyzed + pending < code.Length)
        {
            ProcessJumpDestinationBitmap_Byte((nuint)pending, bitmap.AsSpan(blocks), code.Slice(analyzed));
        }

        return bitmap;
    }

    /// <summary>Looks up each position in the successor map; a position past the block maps to itself.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Advance16(Vector128<byte> successors, Vector128<byte> positions)
        => AdvSimd.Arm64.IsSupported
            ? AdvSimd.Arm64.VectorTableLookupExtension(positions, successors, positions)
            : Vector128.Max(Ssse3.Shuffle(successors, positions), positions);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Successors16(Vector128<sbyte> ops, Vector128<byte> endOffsets,
        out Vector128<byte> n0, out Vector128<byte> n1, out Vector128<byte> n2, out Vector128<byte> n3, out Vector128<byte> n4)
    {
        n0 = Vector128.Max(ops, Vector128.Create((sbyte)PUSHx)).AsByte() + endOffsets;
        n1 = Advance16(n0, n0);
        n2 = Advance16(n1, n1);
        n3 = Advance16(n2, n2);
        n4 = Advance16(n3, n3);
    }

    /// <summary>Returns 0xff in each lane that starts an instruction on the path from <paramref name="entry"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> InstructionStarts16(Vector128<byte> n0, Vector128<byte> n1, Vector128<byte> n2, Vector128<byte> n3,
        Vector128<byte> entry, Vector128<byte> lanes)
    {
        Vector128<byte> position = Descend16(n3, entry, lanes);
        position = Descend16(n2, position, lanes);
        position = Descend16(n1, position, lanes);
        position = Descend16(n0, position, lanes);
        return Vector128.Equals(position, lanes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Descend16(Vector128<byte> successors, Vector128<byte> positions, Vector128<byte> lanes)
    {
        Vector128<byte> next = AdvSimd.Arm64.IsSupported
            ? AdvSimd.Arm64.VectorTableLookupExtension(positions, successors, positions)
            : Ssse3.Shuffle(successors, positions);
        return Vector128.ConditionalSelect(Vector128.LessThanOrEqual(next, lanes), next, positions);
    }

    /// <summary>Packs four vectors of 0x00/0xff lanes into one bitmap word, lane 0 of <paramref name="s0"/> in bit 0.</summary>
    /// <remarks>Weights each lane by its bit in the byte, then three pairwise adds sum eight lanes per byte; sums never carry.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ToBitmapWord(Vector128<byte> s0, Vector128<byte> s1, Vector128<byte> s2, Vector128<byte> s3)
    {
        Vector128<byte> weights = Vector128.Create((byte)1, 2, 4, 8, 16, 32, 64, 128, 1, 2, 4, 8, 16, 32, 64, 128);
        Vector128<byte> pairs01 = AdvSimd.Arm64.AddPairwise(s0 & weights, s1 & weights);
        Vector128<byte> pairs23 = AdvSimd.Arm64.AddPairwise(s2 & weights, s3 & weights);
        Vector128<byte> quads = AdvSimd.Arm64.AddPairwise(pairs01, pairs23);
        return AdvSimd.Arm64.AddPairwise(quads, quads).AsUInt64().ToScalar();
    }

    /// <summary>
    /// Checks if the position is in a code segment.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsJumpDestination(long[] bitvec, int pos)
    {
        int vecIndex = pos >> BitShiftPerInt64;
        // Check if in bounds, Jit will add slightly more expensive exception throwing check if we don't.
        if ((uint)vecIndex >= (uint)bitvec.Length) return false;

        return (bitvec[vecIndex] & (1L << pos)) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MarkJumpDestinations(Span<long> jumpDestinationBitmap, nuint pos, long flags)
    {
        uint offset = (uint)pos >> BitShiftPerInt64;
        ref long segment = ref Unsafe.Add(ref MemoryMarshal.GetReference(jumpDestinationBitmap), offset);
        segment = segment | flags;
    }
}
