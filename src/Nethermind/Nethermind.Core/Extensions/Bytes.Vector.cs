// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Core.Extensions;

public static unsafe partial class Bytes
{
    private static Vector256<byte> ReverseMaskVec
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector256.Create(
            0x08090a0b0c0d0e0ful,
            0x0001020304050607ul,
            0x08090a0b0c0d0e0ful,
            0x0001020304050607ul).AsByte();
    }

    // Internal method that requires AVX2 support - caller must check Avx2.IsSupported before calling
    internal static void Avx2Reverse256InPlace(Span<byte> bytes)
    {
        Debug.Assert(Avx2.IsSupported, "AVX2 must be supported to call Avx2Reverse256InPlace");
        Debug.Assert(bytes.Length == 32, "Input must be exactly 32 bytes");

        fixed (byte* inputPointer = bytes)
        {
            Vector256<byte> inputVector = Avx2.LoadVector256(inputPointer);
            Vector256<byte> resultVector = Avx2.Shuffle(inputVector, ReverseMaskVec);
            resultVector = Avx2.Permute4x64(resultVector.As<byte, ulong>(), 0b01001110).As<ulong, byte>();

            Avx2.Store(inputPointer, resultVector);
        }
    }

    public static void Or(this Span<byte> thisSpan, ReadOnlySpan<byte> valueSpan)
    {
        if (thisSpan.Length != valueSpan.Length)
        {
            ThrowLengthMismatch();
        }

        ref byte thisRef = ref MemoryMarshal.GetReference(thisSpan);
        ref byte valueRef = ref MemoryMarshal.GetReference(valueSpan);

        if (Vector512.IsHardwareAccelerated && thisSpan.Length >= Vector512<byte>.Count)
        {
            for (int i = 0; i < thisSpan.Length - Vector512<byte>.Count; i += Vector512<byte>.Count)
            {
                Vector512<byte> a1 = Vector512.LoadUnsafe(ref Unsafe.Add(ref thisRef, i));
                Vector512<byte> a2 = Vector512.LoadUnsafe(ref Unsafe.Add(ref valueRef, i));
                Vector512.BitwiseOr(a1, a2).StoreUnsafe(ref Unsafe.Add(ref thisRef, i));
            }

            // Always process the final block (covers full or partial tail)
            int offset = thisSpan.Length - Vector512<byte>.Count;
            Vector512<byte> b1 = Vector512.LoadUnsafe(ref Unsafe.Add(ref thisRef, offset));
            Vector512<byte> b2 = Vector512.LoadUnsafe(ref Unsafe.Add(ref valueRef, offset));
            Vector512.BitwiseOr(b1, b2).StoreUnsafe(ref Unsafe.Add(ref thisRef, offset));
        }
        else if (Vector256.IsHardwareAccelerated && thisSpan.Length >= Vector256<byte>.Count)
        {
            for (int i = 0; i < thisSpan.Length - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                Vector256<byte> a1 = Vector256.LoadUnsafe(ref Unsafe.Add(ref thisRef, i));
                Vector256<byte> a2 = Vector256.LoadUnsafe(ref Unsafe.Add(ref valueRef, i));
                Vector256.BitwiseOr(a1, a2).StoreUnsafe(ref Unsafe.Add(ref thisRef, i));
            }

            // Always process the final block (covers full or partial tail)
            int offset = thisSpan.Length - Vector256<byte>.Count;
            Vector256<byte> b1 = Vector256.LoadUnsafe(ref Unsafe.Add(ref thisRef, offset));
            Vector256<byte> b2 = Vector256.LoadUnsafe(ref Unsafe.Add(ref valueRef, offset));
            Vector256.BitwiseOr(b1, b2).StoreUnsafe(ref Unsafe.Add(ref thisRef, offset));
        }
        else if (Vector128.IsHardwareAccelerated && thisSpan.Length >= Vector128<byte>.Count)
        {
            for (int i = 0; i < thisSpan.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                Vector128<byte> a1 = Vector128.LoadUnsafe(ref Unsafe.Add(ref thisRef, i));
                Vector128<byte> a2 = Vector128.LoadUnsafe(ref Unsafe.Add(ref valueRef, i));
                Vector128.BitwiseOr(a1, a2).StoreUnsafe(ref Unsafe.Add(ref thisRef, i));
            }

            // Always process the final block (covers full or partial tail)
            int offset = thisSpan.Length - Vector128<byte>.Count;
            Vector128<byte> b1 = Vector128.LoadUnsafe(ref Unsafe.Add(ref thisRef, offset));
            Vector128<byte> b2 = Vector128.LoadUnsafe(ref Unsafe.Add(ref valueRef, offset));
            Vector128.BitwiseOr(b1, b2).StoreUnsafe(ref Unsafe.Add(ref thisRef, offset));
        }
        else
        {
            // Whole words, then a byte tail: the widest access the target has without SIMD.
            // Correct at any base; the win needs a word-aligned start, which Bloom's byte[256] has.
            int i = 0;
            for (; i <= thisSpan.Length - sizeof(ulong); i += sizeof(ulong))
            {
                ref byte destination = ref Unsafe.Add(ref thisRef, i);
                Unsafe.WriteUnaligned(ref destination,
                    Unsafe.ReadUnaligned<ulong>(ref destination) | Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref valueRef, i)));
            }

            for (; i < thisSpan.Length; i++)
            {
                ref byte destination = ref Unsafe.Add(ref thisRef, i);
                destination = (byte)(destination | Unsafe.Add(ref valueRef, i));
            }
        }
    }

    public static void Xor(this Span<byte> thisSpan, ReadOnlySpan<byte> valueSpan)
    {
        if (thisSpan.Length != valueSpan.Length)
        {
            ThrowLengthMismatch();
        }

        ref byte thisRef = ref MemoryMarshal.GetReference(thisSpan);
        ref byte valueRef = ref MemoryMarshal.GetReference(valueSpan);
        int i = 0;

        // We can't do the fold back technique for xor so need to fall though each size
        if (Vector512.IsHardwareAccelerated)
        {
            for (; i <= thisSpan.Length - Vector512<byte>.Count; i += Vector512<byte>.Count)
            {
                Vector512<byte> b1 = Vector512.LoadUnsafe(ref Unsafe.Add(ref thisRef, i));
                Vector512<byte> b2 = Vector512.LoadUnsafe(ref Unsafe.Add(ref valueRef, i));
                Vector512.Xor(b1, b2).StoreUnsafe(ref Unsafe.Add(ref thisRef, i));
            }

            // Normally a multiple of one vector size so early exit if so
            if (i == thisSpan.Length) return;
        }

        if (Vector256.IsHardwareAccelerated)
        {
            for (; i <= thisSpan.Length - Vector256<byte>.Count; i += Vector256<byte>.Count)
            {
                Vector256<byte> b1 = Vector256.LoadUnsafe(ref Unsafe.Add(ref thisRef, i));
                Vector256<byte> b2 = Vector256.LoadUnsafe(ref Unsafe.Add(ref valueRef, i));
                Vector256.Xor(b1, b2).StoreUnsafe(ref Unsafe.Add(ref thisRef, i));
            }

            // Normally a multiple of one vector size so early exit if so
            if (i == thisSpan.Length) return;
        }

        if (Vector128.IsHardwareAccelerated)
        {
            for (; i <= thisSpan.Length - Vector128<byte>.Count; i += Vector128<byte>.Count)
            {
                Vector128<byte> b1 = Vector128.LoadUnsafe(ref Unsafe.Add(ref thisRef, i));
                Vector128<byte> b2 = Vector128.LoadUnsafe(ref Unsafe.Add(ref valueRef, i));
                Vector128.Xor(b1, b2).StoreUnsafe(ref Unsafe.Add(ref thisRef, i));
            }

            // Normally a multiple of one vector size so early exit if so
            if (i == thisSpan.Length) return;
        }

        // Whole words, then a byte tail, as in Or above.
        for (; i <= thisSpan.Length - sizeof(ulong); i += sizeof(ulong))
        {
            ref byte destination = ref Unsafe.Add(ref thisRef, i);
            Unsafe.WriteUnaligned(ref destination,
                Unsafe.ReadUnaligned<ulong>(ref destination) ^ Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref valueRef, i)));
        }

        for (; i < thisSpan.Length; i++)
        {
            ref byte destination = ref Unsafe.Add(ref thisRef, i);
            destination = (byte)(destination ^ Unsafe.Add(ref valueRef, i));
        }
    }

    public static uint CountBits(this Span<byte> thisSpan)
    {
        uint result = 0;
        if (Popcnt.IsSupported)
        {
            Span<uint> uintSpan = MemoryMarshal.Cast<byte, uint>(thisSpan);
            for (int i = 0; i < uintSpan.Length; i++)
            {
                result += Popcnt.PopCount(uintSpan[i]);
            }
        }
        else
        {
            for (int i = 0; i < thisSpan.Length; i++)
            {
                int n = thisSpan[i];
                while (n > 0)
                {
                    n &= n - 1;
                    result++;
                }
            }
        }

        return result;
    }

    public static int CountLeadingZeroBits(this in Vector256<byte> v)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<byte> cmp = Vector256.Equals(v, Vector256<byte>.Zero);
            uint nonZeroMask = ~cmp.ExtractMostSignificantBits();
            if (nonZeroMask == 0)
                return 256;

            int firstIdx = BitOperations.TrailingZeroCount(nonZeroMask);
            byte b = v.GetElement(firstIdx);
            int lzInByte = BitOperations.LeadingZeroCount(b) - 24;
            return firstIdx * 8 + lzInByte;
        }

        ref ulong parts = ref Unsafe.As<Vector256<byte>, ulong>(ref Unsafe.AsRef(in v));
        ulong part = BinaryPrimitives.ReverseEndianness(parts);
        if (part != 0)
            return BitOperations.LeadingZeroCount(part);

        part = BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref parts, 1));
        if (part != 0)
            return 64 + BitOperations.LeadingZeroCount(part);

        part = BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref parts, 2));
        if (part != 0)
            return 128 + BitOperations.LeadingZeroCount(part);

        part = BinaryPrimitives.ReverseEndianness(Unsafe.Add(ref parts, 3));
        return part == 0 ? 256 : 192 + BitOperations.LeadingZeroCount(part);
    }

    [StackTraceHidden, DoesNotReturn]
    private static void ThrowLengthMismatch() => throw new ArgumentException("Both byte spans has to be same length.");
}
