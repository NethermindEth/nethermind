// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;

namespace Nethermind.Serialization.Json;

public class Hash256Converter : JsonConverter<Hash256>
{
    private readonly bool _strictHexFormat;

    public Hash256Converter(bool strictHexFormat = false)
    {
        _strictHexFormat = strictHexFormat;
    }

    public override Hash256? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {

        byte[]? bytes = ByteArrayConverter.Convert(ref reader, _strictHexFormat);
        return bytes is null ? null : new Hash256(bytes);
    }

    [SkipLocalsInit]
    public override void Write(
        Utf8JsonWriter writer,
        Hash256 keccak,
        JsonSerializerOptions options)
    {
        WriteHashHex(writer, keccak.Bytes);
    }

    /// <summary>
    /// SIMD-accelerated hex encoding for 32-byte hashes using PSHUFB nibble lookup.
    /// Writes raw JSON (including quotes) via WriteRawValue to bypass the encoder entirely.
    /// </summary>
    [SkipLocalsInit]
    internal static void WriteHashHex(Utf8JsonWriter writer, ReadOnlySpan<byte> hash)
    {
        // Raw JSON: '"' + "0x" + 64 hex chars + '"' = 68 bytes
        Span<byte> buf = stackalloc byte[68];
        ref byte b = ref MemoryMarshal.GetReference(buf);

        Unsafe.Add(ref b, 0) = (byte)'"';
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref b, 1), (ushort)0x7830); // "0x" LE

        if (Ssse3.IsSupported)
        {
            Vector128<byte> hexLookup = Vector128.Create(
                (byte)'0', (byte)'1', (byte)'2', (byte)'3',
                (byte)'4', (byte)'5', (byte)'6', (byte)'7',
                (byte)'8', (byte)'9', (byte)'a', (byte)'b',
                (byte)'c', (byte)'d', (byte)'e', (byte)'f');

            ref byte src = ref MemoryMarshal.GetReference(hash);

            // First 16 bytes → 32 hex chars at buf[3..35]
            Vector128<byte> input0 = Vector128.LoadUnsafe(ref src);
            Vector128<byte> hi0 = Sse2.ShiftRightLogical(input0.AsUInt16(), 4).AsByte() & Vector128.Create((byte)0x0F);
            Vector128<byte> lo0 = input0 & Vector128.Create((byte)0x0F);
            Ssse3.Shuffle(hexLookup, Sse2.UnpackLow(hi0, lo0)).StoreUnsafe(ref Unsafe.Add(ref b, 3));
            Ssse3.Shuffle(hexLookup, Sse2.UnpackHigh(hi0, lo0)).StoreUnsafe(ref Unsafe.Add(ref b, 19));

            // Next 16 bytes → 32 hex chars at buf[35..67]
            Vector128<byte> input1 = Vector128.LoadUnsafe(ref src, 16);
            Vector128<byte> hi1 = Sse2.ShiftRightLogical(input1.AsUInt16(), 4).AsByte() & Vector128.Create((byte)0x0F);
            Vector128<byte> lo1 = input1 & Vector128.Create((byte)0x0F);
            Ssse3.Shuffle(hexLookup, Sse2.UnpackLow(hi1, lo1)).StoreUnsafe(ref Unsafe.Add(ref b, 35));
            Ssse3.Shuffle(hexLookup, Sse2.UnpackHigh(hi1, lo1)).StoreUnsafe(ref Unsafe.Add(ref b, 51));
        }
        else
        {
            WriteHexScalar(ref Unsafe.Add(ref b, 3), hash);
        }

        Unsafe.Add(ref b, 67) = (byte)'"';

        writer.WriteRawValue(buf, skipInputValidation: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHexScalar(ref byte dest, ReadOnlySpan<byte> hash)
    {
        for (int i = 0; i < 32; i++)
        {
            int byteVal = hash[i];
            int hi = byteVal >> 4;
            int lo = byteVal & 0xF;
            Unsafe.Add(ref dest, i * 2) = (byte)(hi + 48 + (((9 - hi) >> 31) & 39));
            Unsafe.Add(ref dest, i * 2 + 1) = (byte)(lo + 48 + (((9 - lo) >> 31) & 39));
        }
    }

    // Methods needed to ser/de dictionary keys
    public override Hash256 ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        byte[]? bytes = ByteArrayConverter.Convert(ref reader, _strictHexFormat);
        return bytes is null ? null! : new Hash256(bytes);
    }

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Hash256 value, JsonSerializerOptions options)
    {
        writer.WritePropertyName(value.ToString());
    }
}
