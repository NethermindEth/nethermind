// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Core.Extensions;

/// <summary>Lowercase hex encoding kernels shared by the byte and JSON hex writers.</summary>
internal static class HexEncoder
{
    internal static Vector128<byte> HexLookup128
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector128.Create("0123456789abcdef"u8);
    }

    internal static Vector256<byte> HexLookup256
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector256.Create("0123456789abcdef0123456789abcdef"u8);
    }

    internal static Vector128<byte> ReverseBytes128
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector128.Create((byte)15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0);
    }

    // VPMULTISHIFTQB bit offsets of the high then low nibble of each 16-bit word, four words per qword:
    // for a zero-extended byte they sit at bits 4 and 0, for a byte duplicated into both halves at bits 4 and 8.
    internal const ulong ZeroExtendedByteNibbleShifts = 0x3034_2024_1014_0004;
    internal const ulong DuplicatedByteNibbleShifts = 0x3834_2824_1814_0804;

    /// <summary>Writes the 64 hex chars of 32 bytes, byte <c>i</c> held in 16-bit word <c>i</c> of <paramref name="spread"/>.</summary>
    /// <remarks>
    /// <paramref name="nibbleShifts"/> gives, per output byte, the bit offset in its qword that VPMULTISHIFTQB reads the
    /// nibble from. Only bits 0-3 of each result byte are clean, but VPERMB ignores index bits 6-7 and the table repeats
    /// every 16 bytes, so bits 4-5 do not matter either and no mask is needed.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Avx512VbmiEncode64Nibbles(ref byte dest, Vector512<byte> spread, ulong nibbleShifts)
    {
        Vector512<byte> nibbles = Avx512Vbmi.MultiShift(Vector512.Create(nibbleShifts).AsByte(), spread.AsUInt64());
        Avx512Vbmi.PermuteVar64x8(Vector512.Create("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"u8), nibbles).StoreUnsafe(ref dest);
    }

    /// <summary>Writes 64 hex chars for 32 bytes whose 64-bit lanes are ordered 0, 2, 1, 3.</summary>
    /// <remarks>The unpacks work within 128-bit lanes, so that order makes them emit bytes 0-15, then bytes 16-31.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Avx2Encode32Bytes(ref byte dest, Vector256<byte> laneOrdered)
    {
        Vector256<byte> mask = Vector256.Create((byte)0x0F);
        Vector256<byte> hi = Avx2.ShiftRightLogical(laneOrdered.AsUInt16(), 4).AsByte() & mask;
        Vector256<byte> lo = laneOrdered & mask;
        Vector256<byte> hexLookup = HexLookup256;
        Avx2.Shuffle(hexLookup, Avx2.UnpackLow(hi, lo)).StoreUnsafe(ref dest);
        Avx2.Shuffle(hexLookup, Avx2.UnpackHigh(hi, lo)).StoreUnsafe(ref Unsafe.Add(ref dest, 32));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Ssse3Encode8Bytes(ref byte dest, Vector128<byte> input)
    {
        Vector128<byte> mask = Vector128.Create((byte)0x0F);
        Vector128<byte> hi = Sse2.ShiftRightLogical(input.AsUInt16(), 4).AsByte() & mask;
        Vector128<byte> lo = input & mask;
        Ssse3.Shuffle(HexLookup128, Sse2.UnpackLow(hi, lo)).StoreUnsafe(ref dest);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Ssse3Encode16Bytes(ref byte dest, Vector128<byte> input)
    {
        Vector128<byte> hexLookup = HexLookup128;
        Vector128<byte> mask = Vector128.Create((byte)0x0F);
        Vector128<byte> hi = Sse2.ShiftRightLogical(input.AsUInt16(), 4).AsByte() & mask;
        Vector128<byte> lo = input & mask;
        Ssse3.Shuffle(hexLookup, Sse2.UnpackLow(hi, lo)).StoreUnsafe(ref dest);
        Ssse3.Shuffle(hexLookup, Sse2.UnpackHigh(hi, lo)).StoreUnsafe(ref Unsafe.Add(ref dest, 16));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AdvSimdEncode8Bytes(ref byte dest, Vector128<byte> input)
    {
        Vector128<byte> nibbles = AdvSimd.Arm64.ZipLow(AdvSimd.ShiftRightLogical(input, 4), input & Vector128.Create((byte)0x0F));
        AdvSimd.Arm64.VectorTableLookup(HexLookup128, nibbles).StoreUnsafe(ref dest);
    }

    // The arm64 JIT does not CSE the table load across inlined calls, so callers hoist it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void AdvSimdEncode16Bytes(ref byte dest, Vector128<byte> input, Vector128<byte> hexLookup)
    {
        Vector128<byte> hi = AdvSimd.Arm64.VectorTableLookup(hexLookup, AdvSimd.ShiftRightLogical(input, 4));
        Vector128<byte> lo = AdvSimd.Arm64.VectorTableLookup(hexLookup, input & Vector128.Create((byte)0x0F));
        AdvSimd.Arm64.ZipLow(hi, lo).StoreUnsafe(ref dest);
        AdvSimd.Arm64.ZipHigh(hi, lo).StoreUnsafe(ref Unsafe.Add(ref dest, 16));
    }

    // Two lowercase hex ASCII chars per byte, read as one ushort indexed by the byte.
    private static ReadOnlySpan<byte> HexByteLookup =>
        "000102030405060708090a0b0c0d0e0f"u8 +
        "101112131415161718191a1b1c1d1e1f"u8 +
        "202122232425262728292a2b2c2d2e2f"u8 +
        "303132333435363738393a3b3c3d3e3f"u8 +
        "404142434445464748494a4b4c4d4e4f"u8 +
        "505152535455565758595a5b5c5d5e5f"u8 +
        "606162636465666768696a6b6c6d6e6f"u8 +
        "707172737475767778797a7b7c7d7e7f"u8 +
        "808182838485868788898a8b8c8d8e8f"u8 +
        "909192939495969798999a9b9c9d9e9f"u8 +
        "a0a1a2a3a4a5a6a7a8a9aaabacadaeaf"u8 +
        "b0b1b2b3b4b5b6b7b8b9babbbcbdbebf"u8 +
        "c0c1c2c3c4c5c6c7c8c9cacbcccdcecf"u8 +
        "d0d1d2d3d4d5d6d7d8d9dadbdcdddedf"u8 +
        "e0e1e2e3e4e5e6e7e8e9eaebecedeeef"u8 +
        "f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff"u8;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeByte(ref byte dest, byte value) =>
        Unsafe.WriteUnaligned(ref dest, Unsafe.Add(ref Unsafe.As<byte, ushort>(ref MemoryMarshal.GetReference(HexByteLookup)), value));

    /// <summary>Writes the 8 hex chars of the 4 bytes at <paramref name="src"/>.</summary>
    /// <remarks>The per-byte table beat SWAR arithmetic on both x64 and arm64 without vector instructions.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Encode4BytesScalar(ref byte dest, ref byte src)
    {
        EncodeByte(ref dest, src);
        EncodeByte(ref Unsafe.Add(ref dest, 2), Unsafe.Add(ref src, 1));
        EncodeByte(ref Unsafe.Add(ref dest, 4), Unsafe.Add(ref src, 2));
        EncodeByte(ref Unsafe.Add(ref dest, 6), Unsafe.Add(ref src, 3));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeUlongScalar(ref byte dest, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            EncodeByte(ref Unsafe.Add(ref dest, i * 2), (byte)(value >> (56 - i * 8)));
        }
    }

    /// <summary>Writes the 16 hex chars of <paramref name="value"/>, most significant nibble first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeUlong(ref byte dest, ulong value)
    {
        if (Avx512Vbmi.VL.IsSupported)
        {
            Vector128<byte> spread = Avx512Vbmi.VL.PermuteVar16x8(Vector128.CreateScalarUnsafe(value).AsByte(),
                Vector128.Create((byte)7, 7, 6, 6, 5, 5, 4, 4, 3, 3, 2, 2, 1, 1, 0, 0));
            Vector128<byte> nibbles = Avx512Vbmi.VL.MultiShift(Vector128.Create(DuplicatedByteNibbleShifts).AsByte(), spread.AsUInt64());
            // A 128-bit VPERMB reads index bits 0-3 only.
            Avx512Vbmi.VL.PermuteVar16x8(HexLookup128, nibbles).StoreUnsafe(ref dest);
        }
        else if (Ssse3.IsSupported)
        {
            Ssse3Encode8Bytes(ref dest, Vector128.CreateScalarUnsafe(BinaryPrimitives.ReverseEndianness(value)).AsByte());
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            // As a double this is one fmov; a ulong goes through ins, which merges into the old register value.
            AdvSimdEncode8Bytes(ref dest, Vector128.CreateScalarUnsafe(BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReverseEndianness(value))).AsByte());
        }
        else
        {
            EncodeUlongScalar(ref dest, value);
        }
    }

    /// <summary>Writes the 16 hex chars of the 8 bytes at <paramref name="src"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Encode8Bytes(ref byte dest, ref byte src)
    {
        if (Avx512Vbmi.VL.IsSupported)
        {
            Vector128<byte> spread = Sse41.ConvertToVector128Int16(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref src)).AsByte()).AsByte();
            Vector128<byte> nibbles = Avx512Vbmi.VL.MultiShift(Vector128.Create(ZeroExtendedByteNibbleShifts).AsByte(), spread.AsUInt64());
            Avx512Vbmi.VL.PermuteVar16x8(HexLookup128, nibbles).StoreUnsafe(ref dest);
        }
        else if (Ssse3.IsSupported)
        {
            Ssse3Encode8Bytes(ref dest, Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref src)).AsByte());
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            AdvSimdEncode8Bytes(ref dest, Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<double>(ref src)).AsByte());
        }
        else
        {
            Encode4BytesScalar(ref dest, ref src);
            Encode4BytesScalar(ref Unsafe.Add(ref dest, 8), ref Unsafe.Add(ref src, 4));
        }
    }

    /// <summary>Writes the 32 hex chars of the 16 bytes at <paramref name="src"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Encode16Bytes(ref byte dest, ref byte src)
    {
        if (Avx512Vbmi.VL.IsSupported)
        {
            Vector256<byte> spread = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref src)).AsByte();
            Vector256<byte> nibbles = Avx512Vbmi.VL.MultiShift(Vector256.Create(ZeroExtendedByteNibbleShifts).AsByte(), spread.AsUInt64());
            Avx512Vbmi.VL.PermuteVar32x8(HexLookup256, nibbles).StoreUnsafe(ref dest);
        }
        else if (Ssse3.IsSupported)
        {
            Ssse3Encode16Bytes(ref dest, Vector128.LoadUnsafe(ref src));
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            AdvSimdEncode16Bytes(ref dest, Vector128.LoadUnsafe(ref src), HexLookup128);
        }
        else
        {
            for (int i = 0; i < 16; i += 4)
            {
                Encode4BytesScalar(ref Unsafe.Add(ref dest, i * 2), ref Unsafe.Add(ref src, i));
            }
        }
    }

    /// <summary>Writes the 64 hex chars of the 32 bytes at <paramref name="src"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Encode32Bytes(ref byte dest, ref byte src)
    {
        if (Vector512.IsHardwareAccelerated && Avx512Vbmi.IsSupported)
        {
            Avx512VbmiEncode64Nibbles(ref dest, Avx512BW.ConvertToVector512UInt16(Vector256.LoadUnsafe(ref src)).AsByte(), ZeroExtendedByteNibbleShifts);
        }
        else if (Avx2.IsSupported)
        {
            Avx2Encode32Bytes(ref dest, Avx2.Permute4x64(Vector256.LoadUnsafe(ref src).AsUInt64(), 0b11_01_10_00).AsByte());
        }
        else if (Ssse3.IsSupported)
        {
            Ssse3Encode16Bytes(ref dest, Vector128.LoadUnsafe(ref src));
            Ssse3Encode16Bytes(ref Unsafe.Add(ref dest, 32), Vector128.LoadUnsafe(ref src, 16));
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            Vector128<byte> hexLookup = HexLookup128;
            AdvSimdEncode16Bytes(ref dest, Vector128.LoadUnsafe(ref src), hexLookup);
            AdvSimdEncode16Bytes(ref Unsafe.Add(ref dest, 32), Vector128.LoadUnsafe(ref src, 16), hexLookup);
        }
        else
        {
            for (int i = 0; i < 32; i += 4)
            {
                Encode4BytesScalar(ref Unsafe.Add(ref dest, i * 2), ref Unsafe.Add(ref src, i));
            }
        }
    }

    /// <summary>Writes the <c>2 * src.Length</c> hex chars of <paramref name="src"/> to <paramref name="dest"/>.</summary>
    /// <remarks>
    /// A length that is not a multiple of the block size ends with one more block aligned to the end of the input,
    /// rewriting chars already written with the same values instead of falling back to a per-byte tail.
    /// </remarks>
    internal static void EncodeToHex(ref byte dest, ReadOnlySpan<byte> src)
    {
        ref byte srcRef = ref MemoryMarshal.GetReference(src);
        nuint length = (nuint)src.Length;
        if (!Ssse3.IsSupported && !AdvSimd.Arm64.IsSupported)
        {
            // Overlapping blocks repeat work that only pays off when a block is one vector.
            ref ushort table = ref Unsafe.As<byte, ushort>(ref MemoryMarshal.GetReference(HexByteLookup));
            ref ushort output = ref Unsafe.As<byte, ushort>(ref dest);
            for (; length >= 8; length -= 8)
            {
                Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref output), Unsafe.Add(ref table, srcRef));
                Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref output, 1)), Unsafe.Add(ref table, Unsafe.Add(ref srcRef, 1)));
                Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref output, 2)), Unsafe.Add(ref table, Unsafe.Add(ref srcRef, 2)));
                Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref output, 3)), Unsafe.Add(ref table, Unsafe.Add(ref srcRef, 3)));
                Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref output, 4)), Unsafe.Add(ref table, Unsafe.Add(ref srcRef, 4)));
                Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref output, 5)), Unsafe.Add(ref table, Unsafe.Add(ref srcRef, 5)));
                Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref output, 6)), Unsafe.Add(ref table, Unsafe.Add(ref srcRef, 6)));
                Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref Unsafe.Add(ref output, 7)), Unsafe.Add(ref table, Unsafe.Add(ref srcRef, 7)));
                output = ref Unsafe.Add(ref output, 8);
                srcRef = ref Unsafe.Add(ref srcRef, 8);
            }
            for (; length > 0; length--)
            {
                Unsafe.WriteUnaligned(ref Unsafe.As<ushort, byte>(ref output), Unsafe.Add(ref table, srcRef));
                output = ref Unsafe.Add(ref output, 1);
                srcRef = ref Unsafe.Add(ref srcRef, 1);
            }
            return;
        }

        if (length >= 32)
        {
            nuint last = length - 32;
            for (nuint i = 0; i < last; i += 32)
            {
                Encode32Bytes(ref Unsafe.Add(ref dest, i * 2), ref Unsafe.Add(ref srcRef, i));
            }
            Encode32Bytes(ref Unsafe.Add(ref dest, last * 2), ref Unsafe.Add(ref srcRef, last));
        }
        else if (length >= 16)
        {
            Encode16Bytes(ref dest, ref srcRef);
            Encode16Bytes(ref Unsafe.Add(ref dest, (length - 16) * 2), ref Unsafe.Add(ref srcRef, length - 16));
        }
        else if (length >= 8)
        {
            Encode8Bytes(ref dest, ref srcRef);
            Encode8Bytes(ref Unsafe.Add(ref dest, (length - 8) * 2), ref Unsafe.Add(ref srcRef, length - 8));
        }
        else if (length >= 4)
        {
            Encode4BytesScalar(ref dest, ref srcRef);
            Encode4BytesScalar(ref Unsafe.Add(ref dest, (length - 4) * 2), ref Unsafe.Add(ref srcRef, length - 4));
        }
        else if (length != 0)
        {
            // First, middle and last byte cover every length from 1 to 3 without a loop.
            EncodeByte(ref dest, srcRef);
            EncodeByte(ref Unsafe.Add(ref dest, (length / 2) * 2), Unsafe.Add(ref srcRef, length / 2));
            EncodeByte(ref Unsafe.Add(ref dest, (length - 1) * 2), Unsafe.Add(ref srcRef, length - 1));
        }
    }
}
