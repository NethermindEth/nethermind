// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Nethermind.Core.Threading;

[assembly: InternalsVisibleTo("Nethermind.Evm.Test")]
[assembly: InternalsVisibleTo("Nethermind.Benchmark")]

namespace Nethermind.Evm.CodeAnalysis;

public sealed class JumpDestinationAnalyzer(ReadOnlyMemory<byte> code)
{
    private const int PUSH1 = (int)Instruction.PUSH1;
    private const int PUSHx = PUSH1 - 1;
    private const int JUMPDEST = (int)Instruction.JUMPDEST;
    private const int BitShiftPerInt64 = 6;

    private static readonly long[]? _emptyJumpDestinationBitmap = new long[1];
    private long[]? _jumpDestinationBitmap = code.Length == 0 ? _emptyJumpDestinationBitmap : null;

    private object? _analysisComplete;
    private ReadOnlyMemory<byte> MachineCode { get; } = code;

    public bool ValidateJump(int destination)
    {
        _jumpDestinationBitmap ??= CreateOrWaitForJumpDestinationBitmap();

        // Cast to uint to change negative numbers to very int high numbers
        // Then do length check, this both reduces check by 1 and eliminates the bounds
        // check from accessing the span.
        return (uint)destination < (uint)MachineCode.Length && IsJumpDestination(_jumpDestinationBitmap, destination);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private long[] CreateOrWaitForJumpDestinationBitmap()
    {
        object? previous = Volatile.Read(ref _analysisComplete);
        if (previous is null)
        {
            AnalyzeJumpDestinations(out previous);
        }

        if (previous is ManualResetEventSlim resetEvent)
        {
            WaitForAnalysisToComplete(resetEvent);

            return _jumpDestinationBitmap;
        }

        // Must be the bitmap, and lost check->create benign data race
        return (long[])previous;
    }

    private static void WaitForAnalysisToComplete(ManualResetEventSlim resetEvent)
    {
        // We are waiting, so drop priority to normal (BlockProcessing runs at higher priority).
        using var handle = Thread.CurrentThread.SetNormalPriority();
        // Already in progress, wait for completion.
        resetEvent.Wait();
    }

    private void AnalyzeJumpDestinations(out object previous)
    {
        ManualResetEventSlim analysisComplete = new(initialState: false);
        previous = Interlocked.CompareExchange(ref _analysisComplete, analysisComplete, null);
        if (previous is null)
        {
            // Not already in progress, so start it.
            var bitmap = CreateJumpDestinationBitmap();
            _jumpDestinationBitmap = bitmap;
            // Release the MRES to be GC'd
            Volatile.Write(ref _analysisComplete, bitmap);
            // Signal complete.
            analysisComplete.Set();
            previous = bitmap;
        }
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

    private long[] CreateJumpDestinationBitmap()
    {
        Metrics.IncrementContractsAnalysed();
        ReadOnlySpan<byte> code = MachineCode.Span;

        long[] bitmap = CreateBitmap(code.Length);

        return Vector512<sbyte>.IsSupported && code.Length >= Vector512<sbyte>.Count ?
            PopulateJumpDestinationBitmap_Vector512(bitmap, code) :
            Vector128<sbyte>.IsSupported && code.Length >= Vector128<sbyte>.Count ?
            PopulateJumpDestinationBitmap_Vector128(bitmap, code) :
            PopulateJumpDestinationBitmap_Scalar(bitmap, code);
    }

    internal static long[] CreateBitmap(int codeLength)
        => new long[GetInt64ArrayLengthFromBitLength(codeLength)];

    [SkipLocalsInit]
    internal static long[] PopulateJumpDestinationBitmap_Scalar(long[] bitmap, ReadOnlySpan<byte> code)
    {
        ProcessJumpDestinationBitmap_Scalar(programCounter: 0, bitmap, code);
        return bitmap;
    }

    [SkipLocalsInit]
    private static void ProcessJumpDestinationBitmap_Scalar(nuint programCounter, Span<long> bitmap, ReadOnlySpan<byte> code)
    {
        // We accumulate each array segment to a register and then flush to memory when we move to next.
        long currentFlags = 0;
        while (programCounter < (nuint)code.Length)
        {
            // Grab the instruction from the code; zero length code
            // doesn't enter this method and we check at end of loop if
            // hit the last element and should exit, so skip bounds check
            // access here.
            int op = Unsafe.AddByteOffset(ref MemoryMarshal.GetReference(code), programCounter);
            // Set default programCounter increment to 1 for default case when we don't read a PUSH.
            nuint move = 1;

            if (op == JUMPDEST)
            {
                // Accumulate Jump Destinations to register, shift will wrap and single bit
                // so can shift by the whole programCounter.
                currentFlags |= 1L << (int)programCounter;
            }
            else if ((sbyte)op > PUSHx)
            {
                // Fast forward programCounter by the amount of data the push
                // represents as don't need to analyze data for Jump Destinations.
                move = (nuint)op - PUSH1 + 2;
            }

            nuint nextCounter = programCounter + move;
            // Does the move mean we are moving to new segment of the long array?
            // If we take the current index in flags, and add the move, are we at
            // a new long segment, i.e. a larger than 64 position move.
            if (currentFlags != 0 && ((programCounter & 63) + move >= 64 || nextCounter >= (nuint)code.Length))
            {
                // Moving to next array element (or finishing) assign to array.
                MarkJumpDestinations(bitmap, programCounter, currentFlags);
                // Clear the flags in preparation for the next array segment.
                currentFlags = 0;
            }

            // Move to next instruction.
            programCounter = nextCounter;
        }
    }

    [SkipLocalsInit]
    internal static long[] PopulateJumpDestinationBitmap_Vector512(long[] bitmap, ReadOnlySpan<byte> code)
    {
        Debug.Assert(code.Length > 0);

        ref byte baseRef = ref MemoryMarshal.GetReference(code);
        nuint programCounter = 0;
        ulong carryMask = 0UL;

        nint vectorizedLength = code.Length - Vector512<sbyte>.Count;
        while ((nint)programCounter <= vectorizedLength)
        {
            // Load 64‐byte (512‐bit) chunk
            Vector512<sbyte> opCodes = Unsafe.As<byte, Vector512<sbyte>>(ref Unsafe.Add(ref baseRef, programCounter));

            // Detect Pushes and JumpDests
            ulong pushMask = Vector512.ExtractMostSignificantBits(
                                Vector512.GreaterThan(opCodes, Vector512.Create((sbyte)PUSHx)));
            ulong jmpMask = Vector512.ExtractMostSignificantBits(
                                Vector512.Equals(opCodes, Vector512.Create((sbyte)JUMPDEST)));

            // Clear any push data carried over from the previous chunk
            if (carryMask != 0)
            {
                pushMask &= ~carryMask;
                jmpMask &= ~carryMask;
                carryMask = 0;
            }

            // TODO: Can be improved when .NET supports _mm512_maskz_compress_epi8 https://github.com/dotnet/runtime/issues/87097
            while (pushMask != 0)
            {
                // Peel off each push in turn, clearing its data
                int lane = BitOperations.TrailingZeroCount(pushMask);
                int op = Unsafe.AddByteOffset(ref baseRef, programCounter + (nuint)lane);
                int size = op - PUSH1 + 2;

                // How many bytes fall in this 64-byte chunk
                int inThis = Math.Min(size, Vector512<sbyte>.Count - lane);
                int overflow = size - inThis;

                // Clear bits [lane .. lane+inThis-1] from both masks
                ulong clearThis = (Bmi1.X64.IsSupported ?
                        Bmi2.X64.ZeroHighBits(ulong.MaxValue, (uint)inThis) :
                        ((1UL << inThis) - 1UL))
                    << lane;

                pushMask &= ~clearThis;
                jmpMask &= ~clearThis;

                // If it spilled, mark those lanes for the next chunk
                if (overflow > 0)
                {
                    carryMask = (Bmi1.X64.IsSupported ?
                        Bmi2.X64.ZeroHighBits(ulong.MaxValue, (uint)overflow) :
                        ((1UL << overflow) - 1UL));
                    break;
                }
            }

            // Commit whatever remains
            if (jmpMask != 0)
            {
                MarkJumpDestinations(bitmap, programCounter, (long)jmpMask);
            }

            programCounter += (nuint)Vector512<sbyte>.Count;
        }

        // Skip any carried-over data lanes first
        nuint skip = (nuint)BitOperations.PopCount(carryMask);
        if (programCounter + skip < (nuint)code.Length)
        {
            // Scalar tail for the final (length % 64) bytes
            ProcessJumpDestinationBitmap_Scalar(skip, bitmap.AsSpan((int)programCounter >> BitShiftPerInt64), code.Slice((int)programCounter));
        }

        return bitmap;
    }

    internal static long[] PopulateJumpDestinationBitmap_Vector128(long[] bitmap, ReadOnlySpan<byte> code)
    {
        int programCounter = 0;
        // We accumulate each array segment to a register and then flush to memory when we move to next.
        long currentFlags = 0;
        while (true)
        {
            // Set default programCounter increment to 1 for default case when don't vectorize or read a PUSH.
            int move = 1;
            // We use 128bit rather than Avx or Avx-512 as is optimization for stretch of code without PUSHes.
            // As the vector size increases the chance of there being a PUSH increases which will disable this optimization.
            if (Vector128<sbyte>.IsSupported &&
                // Check not going to read passed end of code.
                programCounter <= code.Length - Vector128<sbyte>.Count &&
                // Are we on an short stride, one quarter of the long flags?
                (programCounter & 15) == 0)
            {
                Vector128<sbyte> data = Unsafe.As<byte, Vector128<sbyte>>(ref Unsafe.AddByteOffset(ref MemoryMarshal.GetReference(code), programCounter));
                // Pushes are 0x60 to 0x7f; converting to signed bytes any instruction higher than PUSH32
                // becomes negative so we can just do a single greater than test to see if any present.
                Vector128<sbyte> compare = Vector128.GreaterThan(data, Vector128.Create((sbyte)PUSHx));
                if (compare == default)
                {
                    // Check the bytes for any JUMPDESTs.
                    Vector128<sbyte> dest = Vector128.Equals(data, Vector128.Create((sbyte)JUMPDEST));
                    // Extract the checks as a set of int flags.
                    uint flags = Vector128.ExtractMostSignificantBits(dest);
                    // Shift up flags by depending which side of long we are on, and merge to current set.
                    currentFlags |= (long)flags << (programCounter & (32 + 16));
                    // Forward programCounter by Vector128 stride.
                    move = Vector128<sbyte>.Count;
                    goto Next;
                }
            }

            // Grab the instruction from the code; zero length code
            // doesn't enter this method and we check at end of loop if
            // hit the last element and should exit, so skip bounds check
            // access here.
            int op = Unsafe.Add(ref MemoryMarshal.GetReference(code), programCounter);

            if (op == JUMPDEST)
            {
                // Accumulate Jump Destinations to register, shift will wrap and single bit
                // so can shift by the whole programCounter.
                currentFlags |= 1L << programCounter;
            }
            else if ((sbyte)op > PUSHx)
            {
                // Fast forward programCounter by the amount of data the push
                // represents as don't need to analyse data for Jump Destinations.
                move = op - PUSH1 + 2;
            }

        Next:
            int nextCounter = programCounter + move;
            // Check if read last item of code; we want to write this out also even if not
            // at a boundary and then we will return the results.
            bool exit = nextCounter >= code.Length;
            // Does the move mean we are moving to new segment of the long array?
            // If we take the current index in flags, and add the move, are we at
            // a new long segment, i.e. a larger than 64 position move.
            if ((programCounter & 63) + move >= 64 || exit)
            {
                // If so write out the flags (if any are set)
                if (currentFlags != 0)
                {
                    // Moving to next array element (or finishing) assign to array.
                    MarkJumpDestinations(bitmap, programCounter, currentFlags);
                    // Clear the flags in preparation for the next array segment.
                    currentFlags = 0;
                }
            }

            if (exit)
            {
                // End of code.
                break;
            }

            // Move to next instruction.
            programCounter = nextCounter;
        }

        return bitmap;
    }

    /// <summary>
    /// Checks if the position is in a code segment.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsJumpDestination(long[] bitvec, int pos)
    {
        int vecIndex = pos >> BitShiftPerInt64;
        // Check if in bounds, Jit will add slightly more expensive exception throwing check if we don't.
        if ((uint)vecIndex >= (uint)bitvec.Length) return false;

        return (bitvec[vecIndex] & (1L << pos)) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MarkJumpDestinations(long[] jumpDestinationBitmap, int pos, long flags)
    {
        uint offset = (uint)pos >> BitShiftPerInt64;
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(jumpDestinationBitmap), offset) |= flags;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MarkJumpDestinations(Span<long> jumpDestinationBitmap, nuint pos, long flags)
    {
        uint offset = (uint)pos >> BitShiftPerInt64;
        Unsafe.Add(ref MemoryMarshal.GetReference(jumpDestinationBitmap), offset) |= flags;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void MarkJumpDestinations(long[] jumpDestinationBitmap, nuint pos, long flags)
    {
        nuint offset = pos >> BitShiftPerInt64;
        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(jumpDestinationBitmap), offset) |= flags;
    }

    public void Execute()
    {
        if (_jumpDestinationBitmap is null && Volatile.Read(ref _analysisComplete) is null)
        {
            ManualResetEventSlim analysisComplete = new(initialState: false);
            if (Interlocked.CompareExchange(ref _analysisComplete, analysisComplete, null) is null)
            {
                // Boost the priority of the thread as block processing may be waiting on this.
                using var handle = Thread.CurrentThread.BoostPriority();

                _jumpDestinationBitmap ??= CreateJumpDestinationBitmap();
                // Release the MRES to be GC'd
                _analysisComplete = _jumpDestinationBitmap;
                // Signal complete.
                analysisComplete.Set();
            }
        }
    }

    public bool RequiresAnalysis => _jumpDestinationBitmap is null;
}
