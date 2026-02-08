// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Json;

public class LongConverter : JsonConverter<long>
{
    public static long FromString(string s)
    {
        if (s is null)
        {
            throw new JsonException("null cannot be assigned to long");
        }

        if (s == Bytes.ZeroHexValue)
        {
            return 0L;
        }

        if (s.StartsWith("0x0"))
        {
            return long.Parse(s.AsSpan(2), NumberStyles.AllowHexSpecifier);
        }

        if (s.StartsWith("0x"))
        {
            Span<char> withZero = new(new char[s.Length - 1]);
            withZero[0] = '0';
            s.AsSpan(2).CopyTo(withZero[1..]);
            return long.Parse(withZero, NumberStyles.AllowHexSpecifier);
        }

        return long.Parse(s, NumberStyles.Integer);
    }

    public override long ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ReadOnlySpan<byte> hex = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
        return FromString(hex);
    }

    public static long FromString(ReadOnlySpan<byte> s)
    {
        if (s.Length == 0)
        {
            throw new JsonException("null cannot be assigned to long");
        }

        if (s.SequenceEqual("0x0"u8))
        {
            return 0L;
        }

        long value;
        if (s.StartsWith("0x"u8))
        {
            s = s[2..];
            if (Utf8Parser.TryParse(s, out value, out _, 'x'))
            {
                return value;
            }
        }
        else if (Utf8Parser.TryParse(s, out value, out _))
        {
            return value;
        }

        ThrowJsonException();
        return default;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowJsonException() => throw new JsonException("hex to long");
    }

    internal static long ReadCore(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetInt64();
        }
        else if (reader.TokenType == JsonTokenType.String)
        {
            if (!reader.HasValueSequence)
            {
                return FromString(reader.ValueSpan);
            }
            else
            {
                return FromString(reader.ValueSequence.ToArray());
            }
        }

        ThrowJsonException();
        return default;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowJsonException() => throw new JsonException();
    }

    public override long Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        return ReadCore(ref reader);
    }

    [SkipLocalsInit]
    public override void Write(
        Utf8JsonWriter writer,
        long value,
        JsonSerializerOptions options)
    {
        switch (ForcedNumberConversion.GetFinalConversion())
        {
            case NumberConversion.Hex:
                if (value == 0)
                {
                    writer.WriteStringValue("0x0"u8);
                }
                else
                {
                    HexWriter.WriteUlongHexRawValue(writer, (ulong)value);
                }
                break;
            case NumberConversion.Decimal:
                writer.WriteStringValue(value == 0 ? "0" : value.ToString(CultureInfo.InvariantCulture));
                break;
            case NumberConversion.Raw:
                writer.WriteNumberValue(value);
                break;
            default:
                throw new NotSupportedException();
        }
    }
}
