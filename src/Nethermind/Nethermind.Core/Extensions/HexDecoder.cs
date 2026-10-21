// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Core.Extensions;

/// <summary>Hex decoding kernels for UTF-8 input of even length.</summary>
internal static class HexDecoder
{
    // Nibble of hex char c at index c - '0', 0x80 for anything else; indices 0x37 up are never hex.
    private static ReadOnlySpan<byte> NibbleTable =>
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80,
        0x80, 10, 11, 12, 13, 14, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80,
        0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80,
        0x80, 10, 11, 12, 13, 14, 15, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80,
        0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80,
        0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80,
        0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80,
        0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80,
    ];

    // PMADDUBSW weights: high nibble * 16 + low nibble per byte pair.
    private static Vector128<sbyte> PairWeights128 => Vector128.Create((short)0x0110).AsSByte();
    private static Vector256<sbyte> PairWeights256 => Vector256.Create((short)0x0110).AsSByte();
    private static Vector512<sbyte> PairWeights512 => Vector512.Create((short)0x0110).AsSByte();

    // 64 chars -> 32 bytes. VPERMI2B reads index bits 0-6, so idx >= 0x80 aliases; it is caught by bit 7 of idx in bad.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector512<byte> Vbmi64(ref byte dest, Vector512<byte> chars, Vector512<byte> tableLo, Vector512<byte> tableHi)
    {
        Vector512<byte> idx = chars - Vector512.Create((byte)'0');
        Vector512<byte> nibbles = Avx512Vbmi.PermuteVar64x8x2(tableLo, idx, tableHi);
        Avx512BW.ConvertToVector256Byte(Avx512BW.MultiplyAddAdjacent(nibbles, PairWeights512).AsUInt16()).StoreUnsafe(ref dest);
        return idx | nibbles;
    }

    // 32 chars -> 16 bytes with a 64-entry table; idx >= 0x40 aliases and is caught by bits 6-7 of idx in bad.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> Vbmi32(Vector256<byte> chars, Vector256<byte> tableLo, Vector256<byte> tableHi, out Vector128<byte> bytes)
    {
        Vector256<byte> idx = chars - Vector256.Create((byte)'0');
        Vector256<byte> nibbles = Avx512Vbmi.VL.PermuteVar32x8x2(tableLo, idx, tableHi);
        bytes = Avx512BW.VL.ConvertToVector128Byte(Avx2.MultiplyAddAdjacent(nibbles, PairWeights256).AsUInt16());
        return idx | nibbles;
    }

    private static bool DecodeVbmi(ref byte src, nuint length, ref byte dest)
    {
        ref byte table = ref MemoryMarshal.GetReference(NibbleTable);
        if (length >= 64)
        {
            Vector512<byte> tableLo = Vector512.LoadUnsafe(ref table);
            Vector512<byte> tableHi = Vector512.LoadUnsafe(ref table, 64);
            Vector512<byte> bad = Vector512<byte>.Zero;
            nuint last = length - 64;
            for (nuint i = 0; i < last; i += 64)
            {
                bad |= Vbmi64(ref Unsafe.Add(ref dest, i / 2), Vector512.LoadUnsafe(ref src, i), tableLo, tableHi);
            }
            bad |= Vbmi64(ref Unsafe.Add(ref dest, last / 2), Vector512.LoadUnsafe(ref src, last), tableLo, tableHi);
            return (bad & Vector512.Create((byte)0x80)) == Vector512<byte>.Zero;
        }

        Vector256<byte> lo = Vector256.LoadUnsafe(ref table);
        Vector256<byte> hi = Vector256.LoadUnsafe(ref table, 32);
        Vector256<byte> check;
        if (length >= 32)
        {
            check = Vbmi32(Vector256.LoadUnsafe(ref src), lo, hi, out Vector128<byte> first);
            check |= Vbmi32(Vector256.LoadUnsafe(ref src, length - 32), lo, hi, out Vector128<byte> last);
            first.StoreUnsafe(ref dest);
            last.StoreUnsafe(ref dest, (length - 32) / 2);
        }
        else
        {
            // Both 16-char halves in one vector: the first and the last 8 bytes of output.
            check = Vbmi32(Vector256.Create(Vector128.LoadUnsafe(ref src), Vector128.LoadUnsafe(ref src, length - 16)), lo, hi, out Vector128<byte> bytes);
            Unsafe.WriteUnaligned(ref dest, bytes.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dest, (length - 16) / 2), bytes.AsUInt64().GetElement(1));
        }
        return (check & Vector256.Create((byte)0xC0)) == Vector256<byte>.Zero;
    }

    // Geoff Langdale and Wojciech Mula's parse: a byte is a hex char exactly when its result is at most 15.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> Nibbles(Vector256<byte> c)
    {
        Vector256<byte> digits = Avx2.SubtractSaturate(c + Vector256.Create((byte)(0xFF - '9')), Vector256.Create((byte)6)) - Vector256.Create((byte)0xF0);
        Vector256<byte> letters = Avx2.AddSaturate((c & Vector256.Create((byte)0xDF)) - Vector256.Create((byte)'A'), Vector256.Create((byte)10));
        return Vector256.Min(digits, letters);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Nibbles(Vector128<byte> c)
    {
        Vector128<byte> digits = Sse2.SubtractSaturate(c + Vector128.Create((byte)(0xFF - '9')), Vector128.Create((byte)6)) - Vector128.Create((byte)0xF0);
        Vector128<byte> letters = Sse2.AddSaturate((c & Vector128.Create((byte)0xDF)) - Vector128.Create((byte)'A'), Vector128.Create((byte)10));
        return Vector128.Min(digits, letters);
    }

    // 64 chars -> 32 bytes; returns the nibbles ORed, so bits 4-7 are set only for a non-hex char.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<byte> Avx2Decode64(ref byte dest, Vector256<byte> c0, Vector256<byte> c1)
    {
        Vector256<byte> n0 = Nibbles(c0);
        Vector256<byte> n1 = Nibbles(c1);
        Vector256<short> w0 = Avx2.MultiplyAddAdjacent(n0, PairWeights256);
        Vector256<short> w1 = Avx2.MultiplyAddAdjacent(n1, PairWeights256);
        // PACKUSWB works per 128-bit lane, VPERMQ restores the order.
        Avx2.Permute4x64(Avx2.PackUnsignedSaturate(w0, w1).AsUInt64(), 0b11_01_10_00).AsByte().StoreUnsafe(ref dest);
        return n0 | n1;
    }

    private static bool DecodeAvx2(ref byte src, nuint length, ref byte dest)
    {
        if (length >= 64)
        {
            Vector256<byte> bad = Vector256<byte>.Zero;
            nuint last = length - 64;
            for (nuint i = 0; i < last; i += 64)
            {
                bad |= Avx2Decode64(ref Unsafe.Add(ref dest, i / 2), Vector256.LoadUnsafe(ref src, i), Vector256.LoadUnsafe(ref src, i + 32));
            }
            bad |= Avx2Decode64(ref Unsafe.Add(ref dest, last / 2), Vector256.LoadUnsafe(ref src, last), Vector256.LoadUnsafe(ref src, last + 32));
            return (bad & Vector256.Create((byte)0xF0)) == Vector256<byte>.Zero;
        }
        return Decode128(ref src, length, ref dest);
    }

    private static (Vector128<byte>, Vector128<byte>, Vector128<byte>, Vector128<byte>) LoadTable128()
    {
        ref byte table = ref MemoryMarshal.GetReference(NibbleTable);
        return (Vector128.LoadUnsafe(ref table), Vector128.LoadUnsafe(ref table, 16), Vector128.LoadUnsafe(ref table, 32), Vector128.LoadUnsafe(ref table, 48));
    }

    // Bits of the Decode32 result that are set only when a char is not hex: bits 4-7 of a Geoff nibble,
    // bits 6-7 of a TBL index or entry (a valid index is below 0x37, and TBL returns 0 from 0x40 up).
    private static Vector128<byte> BadBits128 => Vector128.Create((byte)(AdvSimd.Arm64.IsSupported ? 0xC0 : 0xF0));

    // 32 chars -> 16 bytes; arm64 looks nibbles up in a 64-entry TBL, measured faster there than the nibble math.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> Decode32(Vector128<byte> c0, Vector128<byte> c1, (Vector128<byte>, Vector128<byte>, Vector128<byte>, Vector128<byte>) table, out Vector128<byte> bytes)
    {
        if (Ssse3.IsSupported)
        {
            Vector128<byte> n0 = Nibbles(c0);
            Vector128<byte> n1 = Nibbles(c1);
            bytes = Sse2.PackUnsignedSaturate(Ssse3.MultiplyAddAdjacent(n0, PairWeights128), Ssse3.MultiplyAddAdjacent(n1, PairWeights128));
            return n0 | n1;
        }

        Vector128<byte> i0 = c0 - Vector128.Create((byte)'0');
        Vector128<byte> i1 = c1 - Vector128.Create((byte)'0');
        Vector128<byte> t0 = AdvSimd.Arm64.VectorTableLookup(table, i0);
        Vector128<byte> t1 = AdvSimd.Arm64.VectorTableLookup(table, i1);
        // Even chars are high nibbles; SLI keeps bits 0-3 of the low nibble and inserts the high one above.
        bytes = AdvSimd.ShiftLeftAndInsert(AdvSimd.Arm64.UnzipOdd(t0, t1), AdvSimd.Arm64.UnzipEven(t0, t1), 4);
        return (i0 | t0) | (i1 | t1);
    }

    // SSSE3 and AdvSimd, and AVX2 below 64 chars.
    private static bool Decode128(ref byte src, nuint length, ref byte dest)
    {
        (Vector128<byte>, Vector128<byte>, Vector128<byte>, Vector128<byte>) table = AdvSimd.Arm64.IsSupported ? LoadTable128() : default;
        Vector128<byte> bad;
        if (length >= 32)
        {
            bad = Vector128<byte>.Zero;
            nuint last = length - 32;
            for (nuint i = 0; i < last; i += 32)
            {
                bad |= Decode32(Vector128.LoadUnsafe(ref src, i), Vector128.LoadUnsafe(ref src, i + 16), table, out Vector128<byte> bytes);
                bytes.StoreUnsafe(ref dest, i / 2);
            }
            bad |= Decode32(Vector128.LoadUnsafe(ref src, last), Vector128.LoadUnsafe(ref src, last + 16), table, out Vector128<byte> tail);
            tail.StoreUnsafe(ref dest, last / 2);
        }
        else
        {
            // Both 16-char halves in one call: the first and the last 8 bytes of output.
            bad = Decode32(Vector128.LoadUnsafe(ref src), Vector128.LoadUnsafe(ref src, length - 16), table, out Vector128<byte> bytes);
            Unsafe.WriteUnaligned(ref dest, bytes.AsUInt64().GetElement(0));
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref dest, (length - 16) / 2), bytes.AsUInt64().GetElement(1));
        }
        return (bad & BadBits128) == Vector128<byte>.Zero;
    }

    /// <summary>Decodes <paramref name="length"/> hex chars (even, at least <see cref="MinVectorLength"/>) into <c>length / 2</c> bytes.</summary>
    /// <returns><see langword="false"/> if any char is not a hex digit; <paramref name="dest"/> may then be partly written.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool TryDecodeVector(ref byte src, nuint length, ref byte dest)
    {
        if (Vector512.IsHardwareAccelerated && Avx512Vbmi.IsSupported)
        {
            return DecodeVbmi(ref src, length, ref dest);
        }
        if (Avx2.IsSupported)
        {
            return DecodeAvx2(ref src, length, ref dest);
        }
        return Decode128(ref src, length, ref dest);
    }

    internal static bool IsVectorized => Ssse3.IsSupported || AdvSimd.Arm64.IsSupported;

    // Below 16 chars the table lookup per char was measured faster than a partly filled vector.
    internal const int MinVectorLength = 16;
}
