// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Json
{

    public class Bytes32Converter : JsonConverter<byte[]>
    {
        private const ushort HexPrefix = 0x7830;

        [SkipLocalsInit]
        public override byte[] Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            ReadOnlySpan<byte> hex = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
            if (hex.Length >= 2 && Unsafe.As<byte, ushort>(ref MemoryMarshal.GetReference(hex)) == HexPrefix)
            {
                hex = MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref MemoryMarshal.GetReference(hex), 2), hex.Length - 2);
            }

            if (hex.Length > 64)
            {
                ThrowJsonException();
            }

            if (hex.Length < 64)
            {
                // Use Vector512<byte> as 64-byte buffer instead of stackalloc (avoids GS cookie)
                // Fill with '0' (0x30) using single vector broadcast + store
                Vector512<byte> hex32Storage = Vector512.Create((byte)'0');
                ref byte hex32Ref = ref Unsafe.As<Vector512<byte>, byte>(ref hex32Storage);
                hex.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref hex32Ref, 64 - hex.Length), hex.Length));
                return Bytes.FromUtf8HexString(MemoryMarshal.CreateReadOnlySpan(ref hex32Ref, 64));
            }

            return Bytes.FromUtf8HexString(hex);
        }

        [DoesNotReturn, StackTraceHidden]
        private static void ThrowJsonException() => throw new JsonException();

        [SkipLocalsInit]
        public override void Write(
            Utf8JsonWriter writer,
            byte[] bytes,
            JsonSerializerOptions options)
        {
            // Use Vector256<byte> as 32-byte buffer instead of stackalloc (avoids GS cookie)
            Vector256<byte> dataStorage = default; // Zero-filled (correct for left-padding)
            ref byte dataRef = ref Unsafe.As<Vector256<byte>, byte>(ref dataStorage);
            ReadOnlySpan<byte> data;

            if (bytes is null || bytes.Length < 32)
            {
                if (bytes is not null)
                {
                    int len = bytes.Length;
                    Unsafe.CopyBlockUnaligned(ref Unsafe.Add(ref dataRef, 32 - len),
                        ref MemoryMarshal.GetArrayDataReference(bytes), (uint)len);
                }
                data = MemoryMarshal.CreateReadOnlySpan(ref dataRef, 32);
            }
            else
            {
                data = bytes;
            }

            ByteArrayConverter.Convert(writer, data, skipLeadingZeros: false);
        }
    }
}
