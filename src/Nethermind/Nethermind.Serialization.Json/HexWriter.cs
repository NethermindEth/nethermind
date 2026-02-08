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
    /// Scalar: encode one byte to 2 hex chars.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeByte(ref byte dest, int byteVal)
    {
        int hi = byteVal >> 4;
        int lo = byteVal & 0xF;
        Unsafe.Add(ref dest, 0) = (byte)(hi + 48 + (((9 - hi) >> 31) & 39));
        Unsafe.Add(ref dest, 1) = (byte)(lo + 48 + (((9 - lo) >> 31) & 39));
    }

    /// <summary>
    /// Scalar: encode a ulong (big-endian byte order) to 16 hex chars.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeUlongScalar(ref byte dest, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            EncodeByte(ref Unsafe.Add(ref dest, i * 2), (int)(value >> ((7 - i) << 3)) & 0xFF);
        }
    }

    /// <summary>
    /// Scalar: encode a byte span to hex chars.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EncodeBytesScalar(ref byte dest, ReadOnlySpan<byte> src)
    {
        for (int i = 0; i < src.Length; i++)
        {
            EncodeByte(ref Unsafe.Add(ref dest, i * 2), src[i]);
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
    /// Encode 32 bytes to 64 hex chars, dispatching to SSSE3 or scalar.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Encode32Bytes(ref byte dest, ReadOnlySpan<byte> src)
    {
        if (Ssse3.IsSupported)
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
        // Raw JSON output: '"' + "0x" + 16 hex chars + '"' = 20 bytes max
        Span<byte> buf = stackalloc byte[20];
        ref byte b = ref MemoryMarshal.GetReference(buf);

        EncodeUlong(ref Unsafe.Add(ref b, 3), value);

        // nibbleCount: ceil(significantBits / 4), guaranteed >= 1 since value != 0
        int nibbleCount = (67 - BitOperations.LeadingZeroCount(value)) >> 2;
        int start = 19 - nibbleCount;

        Unsafe.Add(ref b, start - 3) = (byte)'"';
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, start - 2), (ushort)0x7830); // "0x" LE
        Unsafe.Add(ref b, 19) = (byte)'"';

        writer.WriteRawValue(
            MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref b, start - 3), nibbleCount + 4),
            skipInputValidation: true);
    }
}
