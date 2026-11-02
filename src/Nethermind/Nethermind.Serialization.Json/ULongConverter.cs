// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json;

public class ULongConverter : JsonConverter<ulong>
{
    private readonly bool _strictQuantity;
    public ULongConverter() { }
    public ULongConverter(bool strictQuantity) => _strictQuantity = strictQuantity;
    public static ulong FromString(ReadOnlySpan<byte> s) => NumericConverterHelper.Parse<ulong>(s);

    public static ulong FromString(string s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (s.StartsWith("0x", StringComparison.Ordinal))
        {
            ReadOnlySpan<char> digits = s.AsSpan(2);
            // The runtime parser skips trailing NULs but needs a digit, so a bare "0x" reads as zero.
            return digits.TrimEnd('\0').IsEmpty ? 0UL : ulong.Parse(digits, NumberStyles.AllowHexSpecifier);
        }

        return ulong.Parse(s, NumberStyles.Integer);
    }

    [SkipLocalsInit]
    public override void Write(
        Utf8JsonWriter writer,
        ulong value,
        JsonSerializerOptions options) => NumericConverterHelper.Write(writer, value);

    internal static ulong ReadCore(ref Utf8JsonReader reader, bool strictQuantity = false)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (strictQuantity) ThrowJsonException();
            return reader.GetUInt64();
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

    public override ulong Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => ReadCore(ref reader, _strictQuantity);

    public override ulong ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ReadOnlySpan<byte> hex = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
        return FromString(hex);
    }
}
