// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Json;

public class LongConverter : JsonConverter<long>
{
    private readonly bool _strictQuantity;
    public LongConverter() { }
    public LongConverter(bool strictQuantity) => _strictQuantity = strictQuantity;
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

    public static long FromString(ReadOnlySpan<byte> s) => NumericConverterHelper.Parse<long>(s);

    internal static long ReadCore(ref Utf8JsonReader reader, bool strictQuantity = false)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (strictQuantity) ThrowJsonException();
            return reader.GetInt64();
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            ReadOnlySpan<byte> span = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
            if (strictQuantity)
                QuantityValidator.AssertNoLeadingZero(span);
            return FromString(span);
        }

        ThrowJsonException();
        return default;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowJsonException() => throw new JsonException();
    }

    public override long Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => ReadCore(ref reader, _strictQuantity);

    [SkipLocalsInit]
    public override void Write(
        Utf8JsonWriter writer,
        long value,
        JsonSerializerOptions options) => NumericConverterHelper.Write(writer, value);
}
