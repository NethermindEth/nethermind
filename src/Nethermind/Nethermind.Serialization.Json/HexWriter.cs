// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Serialization.Json;

/// <summary>Shared low-level hex encoding primitives used by JSON converters.</summary>
public static class HexWriter
{
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
        Debug.Assert(data.Length == 32);
        HexEncoder.Encode32Bytes(ref Unsafe.Add(ref b, hexOffset), ref MemoryMarshal.GetReference(data));
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
        writer.WriteRawValue(BuildUlongHex(ref rawBuf, value), skipInputValidation: true);
    }

    /// <summary>Builds the quoted, <c>0x</c>-prefixed hex of a non-zero <paramref name="value"/> in <paramref name="buffer"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> BuildUlongHex(ref HexBuffer24 buffer, ulong value)
    {
        ref byte b = ref Unsafe.As<HexBuffer24, byte>(ref buffer);

        HexEncoder.EncodeUlong(ref Unsafe.Add(ref b, 3), value);

        // nibbleCount: ceil(significantBits / 4), guaranteed >= 1 since value != 0
        // nint keeps Unsafe.Add in 64-bit register arithmetic, avoiding movsxd
        nint nibbleCount = (nint)((67 - (uint)BitOperations.LeadingZeroCount(value)) >> 2);
        nint spanStart = 16 - nibbleCount;

        ref byte spanRef = ref Unsafe.Add(ref b, spanStart);
        spanRef = (byte)'"';
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref spanRef, 1), (ushort)0x7830); // "0x" LE
        Unsafe.Add(ref b, 19) = (byte)'"';

        return MemoryMarshal.CreateReadOnlySpan(ref spanRef, (int)nibbleCount + 4);
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
            HexEncoder.Avx512VbmiEncode64Nibbles(ref dest, spread, HexEncoder.DuplicatedByteNibbleShifts);
        }
        else if (Avx2.IsSupported)
        {
            // Limbs to u3, u1, u2, u0, then byte-reverse each limb.
            Vector256<byte> laneOrdered = Avx2.Shuffle(
                Avx2.Permute4x64(Vector256.LoadUnsafe(ref limbs).AsUInt64(), 0b00_10_01_11).AsByte(),
                Vector256.Create(
                    (byte)7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8,
                    7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8));
            HexEncoder.Avx2Encode32Bytes(ref dest, laneOrdered);
        }
        else if (Ssse3.IsSupported)
        {
            Vector128<byte> reverse = HexEncoder.ReverseBytes128;
            HexEncoder.Ssse3Encode16Bytes(ref dest, Ssse3.Shuffle(Vector128.LoadUnsafe(ref limbs, 16), reverse));
            HexEncoder.Ssse3Encode16Bytes(ref Unsafe.Add(ref dest, 32), Ssse3.Shuffle(Vector128.LoadUnsafe(ref limbs), reverse));
        }
        else if (AdvSimd.Arm64.IsSupported)
        {
            Vector128<byte> reverse = HexEncoder.ReverseBytes128;
            Vector128<byte> hexLookup = HexEncoder.HexLookup128;
            HexEncoder.AdvSimdEncode16Bytes(ref dest, AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref limbs, 16), reverse), hexLookup);
            HexEncoder.AdvSimdEncode16Bytes(ref Unsafe.Add(ref dest, 32), AdvSimd.Arm64.VectorTableLookup(Vector128.LoadUnsafe(ref limbs), reverse), hexLookup);
        }
        else
        {
            HexEncoder.EncodeUlongScalar(ref dest, value.u3);
            HexEncoder.EncodeUlongScalar(ref Unsafe.Add(ref dest, 16), value.u2);
            HexEncoder.EncodeUlongScalar(ref Unsafe.Add(ref dest, 32), value.u1);
            HexEncoder.EncodeUlongScalar(ref Unsafe.Add(ref dest, 48), value.u0);
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
            HexEncoder.EncodeToHex(ref MemoryMarshal.GetReference(hex), remaining[..inputLen]);
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
        HexEncoder.EncodeToHex(ref MemoryMarshal.GetReference(hex), data[..inputLen]);
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
    [SkipLocalsInit]
    public static void WriteUlongHexString(IBufferWriter<byte> writer, ulong value)
    {
        if (value == 0)
        {
            writer.Write("\"0x0\""u8);
            return;
        }

        Unsafe.SkipInit(out HexBuffer24 rawBuf);
        writer.Write(BuildUlongHex(ref rawBuf, value));
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
        HexEncoder.EncodeToHex(ref token[3], data);
        token[tokenLength - 1] = (byte)'"';
        writer.WriteRawValue(token[..tokenLength], skipInputValidation: true);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
    }
}
