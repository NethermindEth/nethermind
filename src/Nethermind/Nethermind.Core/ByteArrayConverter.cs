// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Json
{
    public class ByteArrayConverter : JsonConverter<byte[]>
    {
        public override byte[]? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            return Convert(ref reader);
        }

        public static byte[]? Convert(ref Utf8JsonReader reader)
        {
            JsonTokenType tokenType = reader.TokenType;
            if (tokenType == JsonTokenType.None || tokenType == JsonTokenType.Null)
            {
                return null;
            }
            else if (tokenType != JsonTokenType.String)
            {
                ThrowInvalidOperationException();
            }

            int length = reader.ValueSpan.Length;
            byte[]? bytes = null;
            if (length == 0)
            {
                length = checked((int)reader.ValueSequence.Length);
                if (length == 0)
                {
                    return null;
                }

                bytes = ArrayPool<byte>.Shared.Rent(length);
                reader.ValueSequence.CopyTo(bytes);
            }

            ReadOnlySpan<byte> hex = bytes is null ? reader.ValueSpan : bytes.AsSpan(0, length);
            if (hex.StartsWith("0x"u8))
            {
                hex = hex[2..];
            }

            byte[] returnVal = Bytes.FromUtf8HexString(hex);
            if (bytes is not null)
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }

            return returnVal;
        }

        [DoesNotReturn]
        [StackTraceHidden]
        internal static void ThrowInvalidOperationException()
        {
            throw new InvalidOperationException();
        }

        public override void Write(
            Utf8JsonWriter writer,
            byte[] bytes,
            JsonSerializerOptions options)
        {
            Convert(writer, bytes, skipLeadingZeros: false);
        }

        [SkipLocalsInit]
        public static void Convert(Utf8JsonWriter writer, ReadOnlySpan<byte> bytes, bool skipLeadingZeros = true)
        {
            const int maxStackLength = 128;
            const int stackLength = 256;

            int leadingNibbleZeros = skipLeadingZeros ? bytes.CountLeadingZeros() : 0;
            int length = bytes.Length * 2 - leadingNibbleZeros + 4;

            byte[]? array = null;
            if (length > maxStackLength)
            {
                array = ArrayPool<byte>.Shared.Rent(length);
            }

            Span<byte> hex = (array is null ? stackalloc byte[stackLength] : array)[..length];
            hex[^1] = (byte)'"';
            hex[0] = (byte)'"';
            hex[1] = (byte)'0';
            hex[2] = (byte)'x';

            Span<byte> output = hex[3..^1];

            bool extraNibble = (leadingNibbleZeros & 1) != 0;
            ReadOnlySpan<byte> input = bytes.Slice(leadingNibbleZeros / 2);
            input.OutputBytesToByteHex(output, extraNibble: (leadingNibbleZeros & 1) != 0);
            writer.WriteRawValue(hex, skipInputValidation: true);

            if (array is not null)
            {
                ArrayPool<byte>.Shared.Return(array);
            }
        }
    }
}
