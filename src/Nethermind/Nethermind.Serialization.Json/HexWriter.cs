// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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

    // vpermi2b interleaves high/low nibbles across the full 256-bit register, avoiding SSSE3/AVX2 lane-crossing overhead.
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
                (byte)0, 32, 1, 33, 2, 34, 3, 35, 4, 36, 5, 37, 6, 38, 7, 39,
                        8, 40, 9, 41, 10, 42, 11, 43, 12, 44, 13, 45, 14, 46, 15, 47), lo);

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
        if (Avx512Vbmi.VL.IsSupported)
        {
            Vector256<byte> reversed = Avx512Vbmi.VL.PermuteVar32x8(
                Vector256.LoadUnsafe(ref Unsafe.As<UInt256, byte>(ref value)),
                Vector256.Create(
                    (byte)31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16,
                           15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0));
            Avx512VbmiEncode32Bytes(ref dest, reversed);
        }
        else if (Ssse3.IsSupported)
        {
            Ssse3Encode16Bytes(ref dest,
                Vector128.Create(
                    BinaryPrimitives.ReverseEndianness(value.u3),
                    BinaryPrimitives.ReverseEndianness(value.u2)).AsByte());

            Ssse3Encode16Bytes(ref Unsafe.Add(ref dest, 32),
                Vector128.Create(
                    BinaryPrimitives.ReverseEndianness(value.u1),
                    BinaryPrimitives.ReverseEndianness(value.u0)).AsByte());
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
        int tokenLength = 4 + data.Length * 2;
        byte[] rented = ArrayPool<byte>.Shared.Rent(tokenLength);
        try
        {
            rented[0] = (byte)'"';
            rented[1] = (byte)'0';
            rented[2] = (byte)'x';
            EncodeToHex(data, ref rented[3]);
            rented[tokenLength - 1] = (byte)'"';
            writer.WriteRawValue(rented.AsSpan(0, tokenLength), skipInputValidation: true);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static void EncodeToHex(ReadOnlySpan<byte> src, ref byte dest)
    {
        int offset = 0;

        // 32-byte blocks: AVX-512 VBMI or 2x SSSE3 or scalar
        while (offset + 32 <= src.Length)
        {
            Encode32Bytes(ref Unsafe.Add(ref dest, offset * 2), src.Slice(offset, 32));
            offset += 32;
        }

        // 16-byte block via SSSE3
        if (Ssse3.IsSupported && offset + 16 <= src.Length)
        {
            Ssse3Encode16Bytes(ref Unsafe.Add(ref dest, offset * 2),
                Vector128.LoadUnsafe(ref Unsafe.Add(ref MemoryMarshal.GetReference(src), offset)));
            offset += 16;
        }

        // Scalar tail
        if (offset < src.Length)
        {
            EncodeBytesScalar(ref Unsafe.Add(ref dest, offset * 2), src[offset..]);
        }
    }
}
