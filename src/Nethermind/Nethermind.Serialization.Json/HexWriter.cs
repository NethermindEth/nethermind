// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Serialization.Json;

/// <summary>Shared low-level hex encoding primitives used by JSON converters.</summary>
public static class HexWriter
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Ssse3Encode8Bytes(ref byte dest, Vector128<byte> input)
    {
        Vector128<byte> hexLookup = HexLookup128;
        Vector128<byte> mask = Vector128.Create((byte)0x0F);

        Vector128<byte> hi = Sse2.ShiftRightLogical(input.AsUInt16(), 4).AsByte() & mask;
        Vector128<byte> lo = input & mask;
        Ssse3.Shuffle(hexLookup, Sse2.UnpackLow(hi, lo)).StoreUnsafe(ref dest);
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

    private static Vector128<byte> HexLookup128
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector128.Create("0123456789abcdef"u8);
    }

    private static Vector256<byte> HexLookup256
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector256.Create(HexLookup128, HexLookup128);
    }

    private static Vector128<byte> ReverseBytes128
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Vector128.Create((byte)15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0);
    }

    // VPMULTISHIFTQB bit offsets of the high then low nibble of each 16-bit word, four words per qword:
    // for a zero-extended byte they sit at bits 4 and 0, for a byte duplicated into both halves at bits 4 and 8.
    private const ulong ZeroExtendedByteNibbleShifts = 0x3034_2024_1014_0004;
    private const ulong DuplicatedByteNibbleShifts = 0x3834_2824_1814_0804;

    /// <summary>Writes the 64 hex chars of 32 bytes, byte <c>i</c> held in 16-bit word <c>i</c> of <paramref name="spread"/>.</summary>
    /// <remarks>
    /// <paramref name="nibbleShifts"/> gives, per output byte, the bit offset in its qword that VPMULTISHIFTQB reads the
    /// nibble from. Only bits 0-3 of each result byte are clean, but VPERMB ignores index bits 6-7 and the table repeats
    /// every 16 bytes, so bits 4-5 do not matter either and no mask is needed.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Avx512VbmiEncode64Nibbles(ref byte dest, Vector512<byte> spread, ulong nibbleShifts)
    {
        Vector512<byte> nibbles = Avx512Vbmi.MultiShift(Vector512.Create(nibbleShifts).AsByte(), spread.AsUInt64());
        Avx512Vbmi.PermuteVar64x8(Vector512.Create("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"u8), nibbles).StoreUnsafe(ref dest);
    }

    /// <summary>Writes 64 hex chars for 32 bytes whose 64-bit lanes are ordered 0, 2, 1, 3.</summary>
    /// <remarks>The unpacks work within 128-bit lanes, so that order makes them emit bytes 0-15, then bytes 16-31.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Avx2Encode32Bytes(ref byte dest, Vector256<byte> laneOrdered)
    {
        Vector256<byte> mask = Vector256.Create((byte)0x0F);
        Vector256<byte> hi = Avx2.ShiftRightLogical(laneOrdered.AsUInt16(), 4).AsByte() & mask;
        Vector256<byte> lo = laneOrdered & mask;
        Vector256<byte> hexLookup = HexLookup256;
        Avx2.Shuffle(hexLookup, Avx2.UnpackLow(hi, lo)).StoreUnsafe(ref dest);
        Avx2.Shuffle(hexLookup, Avx2.UnpackHigh(hi, lo)).StoreUnsafe(ref Unsafe.Add(ref dest, 32));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AdvSimdEncode8Bytes(ref byte dest, Vector128<byte> input)
    {
        Vector128<byte> nibbles = AdvSimd.Arm64.ZipLow(AdvSimd.ShiftRightLogical(input, 4), input & Vector128.Create((byte)0x0F));
        AdvSimd.Arm64.VectorTableLookup(HexLookup128, nibbles).StoreUnsafe(ref dest);
    }

    // The arm64 JIT does not CSE the table load across inlined calls, so callers hoist it.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AdvSimdEncode16Bytes(ref byte dest, Vector128<byte> input, Vector128<byte> hexLookup)
    {
        Vector128<byte> hi = AdvSimd.Arm64.VectorTableLookup(hexLookup, AdvSimd.ShiftRightLogical(input, 4));
        Vector128<byte> lo = AdvSimd.Arm64.VectorTableLookup(hexLookup, input & Vector128.Create((byte)0x0F));
        AdvSimd.Arm64.ZipLow(hi, lo).StoreUnsafe(ref dest);
        AdvSimd.Arm64.ZipHigh(hi, lo).StoreUnsafe(ref Unsafe.Add(ref dest, 16));
    }

    // Two lowercase hex ASCII chars per byte; replaces branchless arithmetic with one indexed load and one 16-bit store.
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
    internal static void EncodeByte(ref byte dest, int byteVal) => Unsafe.WriteUnaligned(ref dest,
            Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref MemoryMarshal.GetReference(HexByteLookup), byteVal * 2)));

    /// <summary>Returns the 8 hex chars of 4 bytes, the byte in bits 0-7 first.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Encode4BytesSwar(uint bytes)
    {
        ulong spread = bytes;
        spread = (spread | (spread << 16)) & 0x0000_FFFF_0000_FFFF;
        spread = (spread | (spread << 8)) & 0x00FF_00FF_00FF_00FF;
        ulong nibbles = ((spread >> 4) & 0x000F_000F_000F_000F) | ((spread << 8) & 0x0F00_0F00_0F00_0F00);
        // Adding 6 carries into bit 4 exactly for nibbles 10-15, which need 'a' - '0' - 10 = 39 more.
        ulong letters = ((nibbles + 0x0606_0606_0606_0606) >> 4) & 0x0101_0101_0101_0101;
        return nibbles + 0x3030_3030_3030_3030 + letters * 39;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeUlongScalar(ref byte dest, ulong value)
    {
        Unsafe.WriteUnaligned(ref dest, Encode4BytesSwar(BinaryPrimitives.ReverseEndianness((uint)(value >> 32))));
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref dest, 8), Encode4BytesSwar(BinaryPrimitives.ReverseEndianness((uint)value)));
    }

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
            ulong be = BinaryPrimitives.ReverseEndianness(value);
            Ssse3Encode8Bytes(ref dest, Vector128.CreateScalarUnsafe(be).AsByte());
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Encode32Bytes(ref byte dest, ReadOnlySpan<byte> src)
    {
        Debug.Assert(src.Length == 32);
        ref byte srcRef = ref MemoryMarshal.GetReference(src);
        if (Vector512.IsHardwareAccelerated && Avx512Vbmi.IsSupported)
        {
            Avx512VbmiEncode64Nibbles(ref dest, Avx512BW.ConvertToVector512UInt16(Vector256.LoadUnsafe(ref srcRef)).AsByte(), ZeroExtendedByteNibbleShifts);
        }
        else if (Avx2.IsSupported)
        {
            Avx2Encode32Bytes(ref dest, Avx2.Permute4x64(Vector256.LoadUnsafe(ref srcRef).AsUInt64(), 0b11_01_10_00).AsByte());
        }
        else if (Ssse3.IsSupported)
        {
            Ssse3Encode16Bytes(ref dest, Vector128.LoadUnsafe(ref srcRef));
            Ssse3Encode16Bytes(ref Unsafe.Add(ref dest, 32), Vector128.LoadUnsafe(ref srcRef, 16));
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            Vector128<byte> hexLookup = HexLookup128;
            AdvSimdEncode16Bytes(ref dest, Vector128.LoadUnsafe(ref srcRef), hexLookup);
            AdvSimdEncode16Bytes(ref Unsafe.Add(ref dest, 32), Vector128.LoadUnsafe(ref srcRef, 16), hexLookup);
        }
        else
        {
            for (int i = 0; i < 8; i++)
            {
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref dest, i * 8), Encode4BytesSwar(Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref srcRef, i * 4))));
            }
        }
    }

    /// <summary>Writes a 32-byte big-endian value as a JSON string of 64 lowercase hex chars.</summary>
    /// <param name="addHexPrefix">Whether to emit the leading <c>0x</c>. Defaults to <see langword="true"/>.</param>
    [SkipLocalsInit]
    public static void WriteFixed32HexRawValue(Utf8JsonWriter writer, ReadOnlySpan<byte> data, bool addHexPrefix = true)
    {
        Unsafe.SkipInit(out HexBuffer72 rawBuf);
        ref byte b = ref Unsafe.As<HexBuffer72, byte>(ref rawBuf);

        b = (byte)'"';
        nint hexOffset = addHexPrefix ? 3 : 1;
        if (addHexPrefix)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, 1), (ushort)0x7830); // "0x" LE
        }
        Encode32Bytes(ref Unsafe.Add(ref b, hexOffset), data);
        int spanLength = addHexPrefix ? 68 : 66;
        Unsafe.Add(ref b, spanLength - 1) = (byte)'"';

        writer.WriteRawValue(
            MemoryMarshal.CreateReadOnlySpan(ref b, spanLength),
            skipInputValidation: true);
    }

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

    public static void WriteUlongHexStringValue(Utf8JsonWriter writer, ulong value)
    {
        if (value == 0)
        {
            writer.WriteRawValue("\"0x0\""u8, skipInputValidation: true);
            return;
        }

        WriteUlongHexRawValue(writer, value);
    }

    /// <summary>Writes a <see cref="UInt256"/> as a JSON string of lowercase hex chars.</summary>
    /// <param name="addHexPrefix">Whether to emit the leading <c>0x</c>. Defaults to <see langword="true"/>.</param>
    [SkipLocalsInit]
    public static void WriteUInt256HexRawValue(Utf8JsonWriter writer, UInt256 value, bool zeroPadded = false, bool addHexPrefix = true)
    {
        Unsafe.SkipInit(out HexBuffer72 rawBuf);
        ref byte buffer = ref Unsafe.As<HexBuffer72, byte>(ref rawBuf);

        BuildUInt256Hex(ref buffer, value, includeQuotes: true, zeroPadded, addHexPrefix, out nint spanStart, out int spanLength);

        writer.WriteRawValue(
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref buffer, spanStart), spanLength),
            skipInputValidation: true);
    }

    /// <summary>Writes a JSON string containing a <see cref="UInt256"/> as lowercase hex chars.</summary>
    /// <param name="writer">The destination writer.</param>
    /// <param name="value">The value to write.</param>
    /// <param name="zeroPadded">Whether to pad the value to 64 hex chars.</param>
    /// <param name="addHexPrefix">Whether to emit the leading <c>0x</c>. Defaults to <see langword="true"/>.</param>
    [SkipLocalsInit]
    public static void WriteUInt256HexString(IBufferWriter<byte> writer, UInt256 value, bool zeroPadded = false, bool addHexPrefix = true)
    {
        Unsafe.SkipInit(out HexBuffer72 rawBuf);
        ref byte buffer = ref Unsafe.As<HexBuffer72, byte>(ref rawBuf);

        BuildUInt256Hex(ref buffer, value, includeQuotes: true, zeroPadded, addHexPrefix, out nint spanStart, out int spanLength);

        writer.Write(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref buffer, spanStart), spanLength));
    }

    /// <summary>Writes a <see cref="UInt256"/> as a JSON property name of lowercase hex chars.</summary>
    /// <param name="addHexPrefix">Whether to emit the leading <c>0x</c>. Defaults to <see langword="true"/>.</param>
    [SkipLocalsInit]
    public static void WriteUInt256HexPropertyName(Utf8JsonWriter writer, UInt256 value, bool zeroPadded = false, bool addHexPrefix = true)
    {
        Unsafe.SkipInit(out HexBuffer72 rawBuf);
        ref byte buffer = ref Unsafe.As<HexBuffer72, byte>(ref rawBuf);

        BuildUInt256Hex(ref buffer, value, includeQuotes: false, zeroPadded, addHexPrefix, out nint spanStart, out int spanLength);

        writer.WritePropertyName(
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref buffer, spanStart), spanLength));
    }

    public static void WriteUInt256StorageSlot(Utf8JsonWriter writer, in UInt256 key, in UInt256 value)
    {
        WriteUInt256HexPropertyName(writer, key, zeroPadded: true, addHexPrefix: true);
        WriteUInt256HexRawValue(writer, value, zeroPadded: true, addHexPrefix: true);
    }

    // Buffer layout (logical): [opening "?][0x?][64 hex chars][closing "?].
    // EncodeUInt256Hex always writes 64 chars at `hexOffset`; the trimmed significant
    // nibbles end up at the tail, so `spanStart = 64 - nibbleCount` regardless of
    // which optional prefixes are present.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BuildUInt256Hex(ref byte buffer, UInt256 value, bool includeQuotes, bool zeroPadded, bool addHexPrefix, out nint spanStart, out int spanLength)
    {
        nint hexOffset = (includeQuotes ? 1 : 0) + (addHexPrefix ? 2 : 0);
        EncodeUInt256Hex(ref Unsafe.Add(ref buffer, hexOffset), value);

        int nibbleCount = zeroPadded ? 64 : GetSignificantNibbleCount(value);
        spanStart = 64 - nibbleCount;
        ref byte spanRef = ref Unsafe.Add(ref buffer, spanStart);

        nint offset = 0;
        if (includeQuotes)
        {
            spanRef = (byte)'"';
            offset = 1;
        }
        if (addHexPrefix)
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref spanRef, offset), (ushort)0x7830); // "0x" LE
            offset += 2;
        }
        if (includeQuotes)
        {
            Unsafe.Add(ref spanRef, offset + nibbleCount) = (byte)'"';
        }
        spanLength = nibbleCount + (int)offset + (includeQuotes ? 1 : 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetSignificantNibbleCount(UInt256 value)
    {
        int leadingZeroBits;
        if (value.u3 != 0)
        {
            leadingZeroBits = BitOperations.LeadingZeroCount(value.u3);
        }
        else if (value.u2 != 0)
        {
            leadingZeroBits = 64 + BitOperations.LeadingZeroCount(value.u2);
        }
        else if (value.u1 != 0)
        {
            leadingZeroBits = 128 + BitOperations.LeadingZeroCount(value.u1);
        }
        else
        {
            leadingZeroBits = 192 + BitOperations.LeadingZeroCount(value.u0);
        }

        int nibbleCount = (259 - leadingZeroBits) >> 2;
        return nibbleCount == 0 ? 1 : nibbleCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EncodeUInt256Hex(ref byte dest, UInt256 value)
    {
        ref byte limbs = ref Unsafe.As<UInt256, byte>(ref value);
        if (Vector512.IsHardwareAccelerated && Avx512Vbmi.IsSupported)
        {
            // Word i = byte 31 - i in both halves.
            Vector512<byte> spread = Avx512Vbmi.PermuteVar64x8(Vector256.LoadUnsafe(ref limbs).ToVector512Unsafe(),
                Vector512.Create(
                    (byte)31, 31, 30, 30, 29, 29, 28, 28, 27, 27, 26, 26, 25, 25, 24, 24,
                    23, 23, 22, 22, 21, 21, 20, 20, 19, 19, 18, 18, 17, 17, 16, 16,
                    15, 15, 14, 14, 13, 13, 12, 12, 11, 11, 10, 10, 9, 9, 8, 8,
                    7, 7, 6, 6, 5, 5, 4, 4, 3, 3, 2, 2, 1, 1, 0, 0));
            Avx512VbmiEncode64Nibbles(ref dest, spread, DuplicatedByteNibbleShifts);
        }
        else if (Avx2.IsSupported)
        {
            // Limbs to u3, u1, u2, u0, then byte-reverse each limb.
            Vector256<byte> laneOrdered = Avx2.Shuffle(
                Avx2.Permute4x64(Vector256.LoadUnsafe(ref limbs).AsUInt64(), 0b00_10_01_11).AsByte(),
                Vector256.Create(
                    (byte)7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8,
                    7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8));
            Avx2Encode32Bytes(ref dest, laneOrdered);
        }
        else if (Ssse3.IsSupported)
        {
            Vector128<byte> reverse = ReverseBytes128;
            Ssse3Encode16Bytes(ref dest, Ssse3.Shuffle(Vector128.LoadUnsafe(ref limbs, 16), reverse));
            Ssse3Encode16Bytes(ref Unsafe.Add(ref dest, 32), Ssse3.Shuffle(Vector128.LoadUnsafe(ref limbs), reverse));
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            Vector128<byte> reverse = ReverseBytes128;
            Vector128<byte> hexLookup = HexLookup128;
            AdvSimdEncode16Bytes(ref dest, AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref limbs, 16), reverse), hexLookup);
            AdvSimdEncode16Bytes(ref Unsafe.Add(ref dest, 32), AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref limbs), reverse), hexLookup);
        }
        else
        {
            EncodeUlongScalar(ref dest, value.u3);
            EncodeUlongScalar(ref Unsafe.Add(ref dest, 16), value.u2);
            EncodeUlongScalar(ref Unsafe.Add(ref dest, 32), value.u1);
            EncodeUlongScalar(ref Unsafe.Add(ref dest, 48), value.u0);
        }
    }

    // Inline buffers avoid stackalloc GS-cookie overhead on hot hex writers.
    [InlineArray(3)]
    private struct HexBuffer24
    {
        private ulong _element0;
    }

    [InlineArray(9)]
    internal struct HexBuffer72
    {
        private ulong _element0;
    }

    private const int MaxHexRequest = (int)Eip4844Constants.GasPerBlob * 2;

    /// <summary>Writes a large byte array as hex directly into an <see cref="IBufferWriter{T}"/>.</summary>
    public static void WriteHexChunked(IBufferWriter<byte> writer, byte[] data) => WriteHexChunked(writer, (ReadOnlySpan<byte>)data);

    /// <summary>Writes a large byte span as hex directly into an <see cref="IBufferWriter{T}"/>.</summary>
    public static void WriteHexChunked(IBufferWriter<byte> writer, ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<byte> remaining = data;
        while (remaining.Length > 0)
        {
            Span<byte> hex = writer.GetSpan(Math.Min(remaining.Length * 2, MaxHexRequest));
            int inputLen = Math.Min(remaining.Length, hex.Length / 2);
            EncodeToHex(remaining[..inputLen], ref MemoryMarshal.GetReference(hex));
            writer.Advance(inputLen * 2);

            remaining = remaining[inputLen..];
        }
    }

    /// <summary>Writes a small byte array as hex in a single span into an <see cref="IBufferWriter{T}"/>.</summary>
    public static void WriteHexSmall(IBufferWriter<byte> writer, byte[] data) => WriteHexSmall(writer, (ReadOnlySpan<byte>)data);

    /// <summary>Writes a small byte span as hex in a single span into an <see cref="IBufferWriter{T}"/>.</summary>
    public static void WriteHexSmall(IBufferWriter<byte> writer, ReadOnlySpan<byte> data)
    {
        int hexLen = data.Length * 2;
        Span<byte> hex = writer.GetSpan(hexLen);
        int inputLen = Math.Min(data.Length, hex.Length / 2);
        EncodeToHex(data[..inputLen], ref MemoryMarshal.GetReference(hex));
        writer.Advance(inputLen * 2);
    }

    /// <summary>Writes a JSON string containing a <c>0x</c>-prefixed lowercase hex byte sequence.</summary>
    public static void WriteHexString(IBufferWriter<byte> writer, ReadOnlySpan<byte> data, bool chunked)
    {
        writer.Write("\"0x"u8);
        if (chunked) WriteHexChunked(writer, data);
        else WriteHexSmall(writer, data);
        writer.Write("\""u8);
    }

    /// <summary>Writes a JSON string containing a <c>0x</c>-prefixed lowercase hex unsigned integer.</summary>
    public static void WriteUlongHexString(IBufferWriter<byte> writer, ulong value)
    {
        if (value == 0)
        {
            writer.Write("\"0x0\""u8);
            return;
        }

        Span<byte> buffer = writer.GetSpan(20);
        buffer[0] = (byte)'"';
        buffer[1] = (byte)'0';
        buffer[2] = (byte)'x';
        value.TryFormat(buffer[3..], out int bytesWritten, "x");
        buffer[bytesWritten + 3] = (byte)'"';
        writer.Advance(bytesWritten + 4);
    }

    [SkipLocalsInit]
    public static void WriteHexStringValue(Utf8JsonWriter writer, ReadOnlySpan<byte> data)
    {
        const int StackThreshold = 512;
        int tokenLength = 4 + data.Length * 2;
        // data is unbounded (e.g. a whole memory snapshot), so only stay on the stack while small.
        byte[]? rented = tokenLength > StackThreshold ? ArrayPool<byte>.Shared.Rent(tokenLength) : null;
        Span<byte> token = rented is not null ? rented : stackalloc byte[StackThreshold];
        token[0] = (byte)'"';
        token[1] = (byte)'0';
        token[2] = (byte)'x';
        EncodeToHex(data, ref token[3]);
        token[tokenLength - 1] = (byte)'"';
        writer.WriteRawValue(token[..tokenLength], skipInputValidation: true);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
    }

    private static void EncodeToHex(ReadOnlySpan<byte> src, ref byte dest)
    {
        int offset = 0;

        while (offset + 32 <= src.Length)
        {
            Encode32Bytes(ref Unsafe.Add(ref dest, offset * 2), src.Slice(offset, 32));
            offset += 32;
        }

        // 16-byte block via SSSE3 or AdvSimd
        if (Ssse3.IsSupported && offset + 16 <= src.Length)
        {
            Ssse3Encode16Bytes(ref Unsafe.Add(ref dest, offset * 2),
                Vector128.LoadUnsafe(ref Unsafe.Add(ref MemoryMarshal.GetReference(src), offset)));
            offset += 16;
        }
        else if (AdvSimd.Arm64.IsSupported && offset + 16 <= src.Length)
        {
            AdvSimdEncode16Bytes(ref Unsafe.Add(ref dest, offset * 2),
                Vector128.LoadUnsafe(ref Unsafe.Add(ref MemoryMarshal.GetReference(src), offset)), HexLookup128);
            offset += 16;
        }

        // Scalar tail
        if (offset < src.Length)
        {
            EncodeBytesScalar(ref Unsafe.Add(ref dest, offset * 2), src[offset..]);
        }
    }
}
