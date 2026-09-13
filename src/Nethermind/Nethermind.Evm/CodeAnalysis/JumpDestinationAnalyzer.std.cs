// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nethermind.Evm.CodeAnalysis;

/// <remarks>
/// The word-at-a-time scan the standard build takes. The zkEVM build compiles its own
/// <c>PopulateJumpDestinationBitmap_Scalar</c> and <c>ProcessJumpDestinationBitmap_Byte</c> instead - see
/// <c>JumpDestinationAnalyzer.zkevm.cs</c>, kept honest by <c>GuestJumpDestinationTests</c>.
/// </remarks>
public sealed partial class JumpDestinationAnalyzer
{
    private const int BytesPerUInt64 = sizeof(ulong);
    private const int ScalarWordThreshold = 64;
    private const ulong ByteHighBits = 0x8080808080808080UL;
    private const ulong ByteLowBits = 0x7f7f7f7f7f7f7f7fUL;
    private const ulong JumpDestBytes = 0x5b5b5b5b5b5b5b5bUL;
    private const ulong PackByteHighBits = 0x0002040810204081UL;

    [SkipLocalsInit]
    internal static long[] PopulateJumpDestinationBitmap_Scalar(long[] bitmap, ReadOnlySpan<byte> code)
    {
        if (code.Length < ScalarWordThreshold)
        {
            ProcessJumpDestinationBitmap_Byte(programCounter: 0, bitmap, code);
        }
        // A PUSH in the first word predicts code where the byte scanner is cheaper on the JIT.
        else if (ContainsPushByte(Unsafe.As<byte, ulong>(ref MemoryMarshal.GetReference(code))))
        {
            ProcessJumpDestinationBitmap_Byte(programCounter: 0, bitmap, code);
        }
        else
        {
            ProcessJumpDestinationBitmap_Scalar(bitmap, code);
        }

        return bitmap;
    }

    [SkipLocalsInit]
    private static void ProcessJumpDestinationBitmap_Scalar(Span<long> bitmap, ReadOnlySpan<byte> code)
    {
        long currentFlags = 0;
        nuint flagsPosition = 0;
        nuint length = (nuint)code.Length;
        nuint wordEnd = length & ~(nuint)(BytesPerUInt64 - 1);
        ref byte codeRef = ref MemoryMarshal.GetReference(code);
        nuint programCounter = 0;
        ulong byteHighBits = ByteHighBits;
        ulong jumpDestBytes = JumpDestBytes;
        ulong byteOnes = 0x0101010101010101UL;

        while (programCounter < wordEnd)
        {
            int firstLane = (int)(programCounter & (BytesPerUInt64 - 1));
            nuint wordStart = programCounter;
            ulong opCodes;
            ulong remainingOpCodes;
            if (firstLane == 0)
            {
                opCodes = Unsafe.As<byte, ulong>(ref Unsafe.AddByteOffset(ref codeRef, programCounter));
                remainingOpCodes = opCodes;
            }
            else
            {
                // Keep loads aligned after a PUSH by re-reading the containing word and discarding consumed lanes.
                wordStart -= (nuint)firstLane;
                opCodes = Unsafe.As<byte, ulong>(ref Unsafe.AddByteOffset(ref codeRef, wordStart));
                remainingOpCodes = opCodes >> (firstLane * 8);
            }

            ulong pushHighBits = ~remainingOpCodes & (remainingOpCodes << 1) & (remainingOpCodes << 2) & byteHighBits;
            int endLane = BytesPerUInt64;
            if (pushHighBits != 0)
            {
                int currentOp = (byte)remainingOpCodes;
                if ((uint)(currentOp - PUSH1) <= PUSH32 - PUSH1)
                {
                    programCounter += (nuint)currentOp - PUSH1 + 2;
                    continue;
                }

                endLane = firstLane + FirstSetBit((byte)((pushHighBits * PackByteHighBits) >> 56));
            }

            ulong jumpCandidates = opCodes ^ jumpDestBytes;
            if (((jumpCandidates - byteOnes) & ~jumpCandidates & byteHighBits) != 0)
            {
                uint activeLanes = ((1U << endLane) - 1U) & ~((1U << firstLane) - 1U);
                uint jumpMask = MatchBytes(jumpCandidates) & activeLanes;
                if (jumpMask != 0)
                {
                    if ((wordStart ^ flagsPosition) >> BitShiftPerInt64 != 0 && currentFlags != 0)
                    {
                        MarkJumpDestinations(bitmap, flagsPosition, currentFlags);
                        currentFlags = 0;
                    }

                    currentFlags |= (long)jumpMask << (int)wordStart;
                    flagsPosition = wordStart;
                }
            }

            if (pushHighBits == 0)
            {
                programCounter = wordStart + BytesPerUInt64;
            }
            else
            {
                int pushOp = (byte)(opCodes >> (endLane * 8));
                programCounter = wordStart + (nuint)(endLane + pushOp - PUSH1 + 2);
            }
        }

        while (programCounter < length)
        {
            int op = Unsafe.AddByteOffset(ref codeRef, programCounter);
            if (op == JUMPDEST)
            {
                if ((programCounter ^ flagsPosition) >> BitShiftPerInt64 != 0 && currentFlags != 0)
                {
                    MarkJumpDestinations(bitmap, flagsPosition, currentFlags);
                    currentFlags = 0;
                }

                currentFlags |= 1L << (int)programCounter;
                flagsPosition = programCounter;
                programCounter++;
            }
            else if ((uint)(op - PUSH1) <= PUSH32 - PUSH1)
            {
                programCounter += (nuint)op - PUSH1 + 2;
            }
            else
            {
                programCounter++;
            }
        }

        if (currentFlags != 0)
        {
            MarkJumpDestinations(bitmap, flagsPosition, currentFlags);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint MatchBytes(ulong value)
    {
        ulong matches = ~(value | ((value & ByteLowBits) + ByteLowBits)) & ByteHighBits;
        return (uint)((matches * PackByteHighBits) >> 56);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ContainsPushByte(ulong opCodes)
        => (~opCodes & (opCodes << 1) & (opCodes << 2) & ByteHighBits) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FirstSetBit(byte value)
    {
        int index = 0;
        if ((value & 0x0f) == 0)
        {
            index = 4;
            value >>= 4;
        }

        if ((value & 0x03) == 0)
        {
            index += 2;
            value >>= 2;
        }

        return index + 1 - (value & 1);
    }

    [SkipLocalsInit]
    private static void ProcessJumpDestinationBitmap_Byte(nuint programCounter, Span<long> bitmap, ReadOnlySpan<byte> code)
    {
        long currentFlags = 0;
        nuint flagsPosition = 0;
        nuint length = (nuint)code.Length;
        ref byte codeRef = ref MemoryMarshal.GetReference(code);
        while (programCounter < length)
        {
            int op = Unsafe.AddByteOffset(ref codeRef, programCounter);
            if ((uint)(op - JUMPDEST) > PUSH32 - JUMPDEST)
            {
                programCounter++;
                continue;
            }

            if (op == JUMPDEST)
            {
                if ((programCounter ^ flagsPosition) >> BitShiftPerInt64 != 0 && currentFlags != 0)
                {
                    MarkJumpDestinations(bitmap, flagsPosition, currentFlags);
                    currentFlags = 0;
                }

                currentFlags |= 1L << (int)programCounter;
                flagsPosition = programCounter;
                programCounter++;
            }
            else if (op >= PUSH1)
            {
                programCounter += (nuint)op - PUSH1 + 2;
            }
            else
            {
                programCounter++;
            }
        }

        if (currentFlags != 0)
        {
            MarkJumpDestinations(bitmap, flagsPosition, currentFlags);
        }
    }
}
