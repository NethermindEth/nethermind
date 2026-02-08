// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;

namespace Nethermind.Serialization.Json;

/// <summary>
/// Shared low-level hex encoding primitives used by JSON converters.
/// </summary>
internal static class HexWriter
{
    /// <summary>
    /// Encode the low 8 bytes of a Vector128 to 16 hex chars using SSSE3 PSHUFB.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Ssse3Encode8Bytes(ref byte dest, Vector128<byte> input)
    {
        Vector128<byte> hexLookup = Vector128.Create(
            (byte)'0', (byte)'1', (byte)'2', (byte)'3',
            (byte)'4', (byte)'5', (byte)'6', (byte)'7',
            (byte)'8', (byte)'9', (byte)'a', (byte)'b',
            (byte)'c', (byte)'d', (byte)'e', (byte)'f');
        Vector128<byte> mask = Vector128.Create((byte)0x0F);

        Vector128<byte> hi = Sse2.ShiftRightLogical(input.AsUInt16(), 4).AsByte() & mask;
        Vector128<byte> lo = input & mask;
        Ssse3.Shuffle(hexLookup, Sse2.UnpackLow(hi, lo)).StoreUnsafe(ref dest);
    }

    /// <summary>
    /// Encode 16 bytes of a Vector128 to 32 hex chars using SSSE3 PSHUFB.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Ssse3Encode16Bytes(ref byte dest, Vector128<byte> input)
    {
        Vector128<byte> hexLookup = Vector128.Create(
            (byte)'0', (byte)'1', (byte)'2', (byte)'3',
            (byte)'4', (byte)'5', (byte)'6', (byte)'7',
            (byte)'8', (byte)'9', (byte)'a', (byte)'b',
            (byte)'c', (byte)'d', (byte)'e', (byte)'f');
        Vector128<byte> mask = Vector128.Create((byte)0x0F);

        Vector128<byte> hi = Sse2.ShiftRightLogical(input.AsUInt16(), 4).AsByte() & mask;
        Vector128<byte> lo = input & mask;
        Ssse3.Shuffle(hexLookup, Sse2.UnpackLow(hi, lo)).StoreUnsafe(ref dest);
        Ssse3.Shuffle(hexLookup, Sse2.UnpackHigh(hi, lo)).StoreUnsafe(ref Unsafe.Add(ref dest, 16));
    }

    /// <summary>
    /// Encode 32 bytes to 64 hex chars using AVX-512 VBMI cross-lane byte permutation.
    /// vpermi2b does arbitrary byte interleave across the full 256-bit register in a single
    /// instruction, eliminating the UnpackLow/UnpackHigh + lane-crossing overhead of SSSE3/AVX2.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Avx512VbmiEncode32Bytes(ref byte dest, Vector256<byte> input)
    {
        Vector256<byte> mask = Vector256.Create((byte)0x0F);
        Vector256<byte> hi = Avx2.ShiftRightLogical(input.AsUInt16(), 4).AsByte() & mask;
        Vector256<byte> lo = input & mask;

        // vpermi2b: pick hi[i], lo[i] pairs across full 256-bit width
        // indices 0-31 select from hi, 32-63 select from lo
        Vector256<byte> interleaved0 = Avx512Vbmi.VL.PermuteVar32x8x2(hi,
            Vector256.Create(
                (byte) 0, 32,  1, 33,  2, 34,  3, 35,  4, 36,  5, 37,  6, 38,  7, 39,
                        8, 40,  9, 41, 10, 42, 11, 43, 12, 44, 13, 45, 14, 46, 15, 47), lo);

        Vector256<byte> interleaved1 = Avx512Vbmi.VL.PermuteVar32x8x2(hi,
            Vector256.Create(
                (byte)16, 48, 17, 49, 18, 50, 19, 51, 20, 52, 21, 53, 22, 54, 23, 55,
                       24, 56, 25, 57, 26, 58, 27, 59, 28, 60, 29, 61, 30, 62, 31, 63), lo);

        // vpshufb: nibble-to-hex lookup (works within 128-bit lanes, lookup replicated in both)
        Vector256<byte> hexLookup = Vector256.Create(
            (byte)'0', (byte)'1', (byte)'2', (byte)'3',
            (byte)'4', (byte)'5', (byte)'6', (byte)'7',
            (byte)'8', (byte)'9', (byte)'a', (byte)'b',
            (byte)'c', (byte)'d', (byte)'e', (byte)'f',
            (byte)'0', (byte)'1', (byte)'2', (byte)'3',
            (byte)'4', (byte)'5', (byte)'6', (byte)'7',
            (byte)'8', (byte)'9', (byte)'a', (byte)'b',
            (byte)'c', (byte)'d', (byte)'e', (byte)'f');

        Avx2.Shuffle(hexLookup, interleaved0).StoreUnsafe(ref dest);
        Avx2.Shuffle(hexLookup, interleaved1).StoreUnsafe(ref Unsafe.Add(ref dest, 32));
    }

    /// <summary>
    /// 512-byte lookup table: for byte value i, HexByteLookup[i*2] and [i*2+1] are the
    /// two lowercase hex ASCII chars. Single indexed load + 16-bit store per byte,
    /// replacing ~10 ALU ops of a branchless arithmetic approach.
    /// </summary>
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

    /// <summary>
    /// Scalar: encode one byte to 2 hex chars via lookup table.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeByte(ref byte dest, int byteVal)
    {
        Unsafe.WriteUnaligned(ref dest,
            Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref MemoryMarshal.GetReference(HexByteLookup), byteVal * 2)));
    }

    /// <summary>
    /// Scalar: encode a ulong (big-endian byte order) to 16 hex chars.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeUlongScalar(ref byte dest, ulong value)
    {
        ref byte lookup = ref MemoryMarshal.GetReference(HexByteLookup);
        for (int i = 0; i < 8; i++)
        {
            int byteVal = (int)(value >> ((7 - i) << 3)) & 0xFF;
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dest, i * 2),
                Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref lookup, byteVal * 2)));
        }
    }

    /// <summary>
    /// Scalar: encode a byte span to hex chars.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeBytesScalar(ref byte dest, ReadOnlySpan<byte> src)
    {
        ref byte lookup = ref MemoryMarshal.GetReference(HexByteLookup);
        for (int i = 0; i < src.Length; i++)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dest, i * 2),
                Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref lookup, src[i] * 2)));
        }
    }

    /// <summary>
    /// Encode a ulong to 16 hex chars, dispatching to SSSE3 or scalar.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeUlong(ref byte dest, ulong value)
    {
        if (Ssse3.IsSupported)
        {
            ulong be = BinaryPrimitives.ReverseEndianness(value);
            Ssse3Encode8Bytes(ref dest, Vector128.CreateScalarUnsafe(be).AsByte());
        }
        else
        {
            EncodeUlongScalar(ref dest, value);
        }
    }

    /// <summary>
    /// Encode 32 bytes to 64 hex chars, dispatching to AVX-512 VBMI, SSSE3, or scalar.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Encode32Bytes(ref byte dest, ReadOnlySpan<byte> src)
    {
        if (Avx512Vbmi.VL.IsSupported)
        {
            Avx512VbmiEncode32Bytes(ref dest, Vector256.LoadUnsafe(ref MemoryMarshal.GetReference(src)));
        }
        else if (Ssse3.IsSupported)
        {
            ref byte srcRef = ref MemoryMarshal.GetReference(src);
            Ssse3Encode16Bytes(ref dest, Vector128.LoadUnsafe(ref srcRef));
            Ssse3Encode16Bytes(ref Unsafe.Add(ref dest, 32), Vector128.LoadUnsafe(ref srcRef, 16));
        }
        else
        {
            EncodeBytesScalar(ref dest, src);
        }
    }

    /// <summary>
    /// Write a non-zero ulong as a hex JSON string value ("0x...") using WriteRawValue.
    /// Used by LongConverter and ULongConverter.
    /// </summary>
    [SkipLocalsInit]
    internal static void WriteUlongHexRawValue(Utf8JsonWriter writer, ulong value)
    {
        // Use InlineArray to avoid GS cookie overhead from stackalloc
        Unsafe.SkipInit(out HexBuffer24 rawBuf);
        ref byte b = ref Unsafe.As<HexBuffer24, byte>(ref rawBuf);

        EncodeUlong(ref Unsafe.Add(ref b, 3), value);

        // nibbleCount: ceil(significantBits / 4), guaranteed >= 1 since value != 0
        // nint keeps Unsafe.Add in 64-bit register arithmetic, avoiding movsxd
        nint nibbleCount = (nint)((67 - (uint)BitOperations.LeadingZeroCount(value)) >> 2);
        nint spanStart = 16 - nibbleCount;

        ref byte spanRef = ref Unsafe.Add(ref b, spanStart);
        spanRef = (byte)'"';
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref spanRef, 1), (ushort)0x7830); // "0x" LE
        Unsafe.Add(ref b, 19) = (byte)'"';

        writer.WriteRawValue(
            MemoryMarshal.CreateReadOnlySpan(ref spanRef, (int)nibbleCount + 4),
            skipInputValidation: true);
    }

    /// <summary>
    /// 24-byte inline buffer for ulong hex encoding (20 bytes needed, rounded up to
    /// 3 x 8-byte ulong elements for alignment). Used instead of stackalloc to avoid
    /// GS cookie (stack canary) overhead. The JIT inserts a cookie write in the prologue
    /// and a verify + CORINFO_HELP_FAIL_FAST call in the epilogue for every stackalloc
    /// buffer, adding ~35 bytes per method. Inline array structs are treated as regular
    /// locals and avoid this.
    /// </summary>
    [InlineArray(3)]
    private struct HexBuffer24
    {
        private ulong _element0;
    }

    /// <summary>
    /// 72-byte inline buffer for hash/UInt256 hex encoding (68 bytes needed, rounded up
    /// to 9 x 8-byte ulong elements for alignment). Used instead of stackalloc to avoid
    /// GS cookie (stack canary) overhead. The JIT inserts a cookie write in the prologue
    /// and a verify + CORINFO_HELP_FAIL_FAST call in the epilogue for every stackalloc
    /// buffer, adding ~35 bytes per method. Inline array structs are treated as regular
    /// locals and avoid this.
    /// </summary>
    [InlineArray(9)]
    internal struct HexBuffer72
    {
        private ulong _element0;
    }
}
