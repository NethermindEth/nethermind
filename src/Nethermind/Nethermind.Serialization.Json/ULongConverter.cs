// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace Nethermind.Serialization.Json;

public class ULongConverter : JsonConverter<ulong>
{
    public static ulong FromString(ReadOnlySpan<byte> s) => NumericConverterHelper.Parse<ulong>(s);

    public static ulong FromString(string s)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (s == Nethermind.Core.Extensions.Bytes.ZeroHexValue)
        {
            return 0UL;
        }

        if (s.StartsWith("0x0"))
        {
            throw new JsonException("hex to ulong");
        }

        if (s.StartsWith("0x"))
        {
            Span<char> withZero = new(new char[s.Length - 1]);
            withZero[0] = '0';
            s.AsSpan(2).CopyTo(withZero[1..]);
            return ulong.Parse(withZero, NumberStyles.AllowHexSpecifier);
        }

        return ulong.Parse(s, NumberStyles.Integer);
    }

    [SkipLocalsInit]
    public override void Write(
        Utf8JsonWriter writer,
        ulong value,
        JsonSerializerOptions options) => NumericConverterHelper.Write(writer, value);

    internal static ulong ReadCore(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetUInt64();
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return !reader.HasValueSequence
                ? NumericConverterHelper.Parse<ulong>(reader.ValueSpan)
                : NumericConverterHelper.Parse<ulong>(reader.ValueSequence.ToArray());
        }

        ThrowJsonException();
        return default;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowJsonException() => throw new JsonException();
    }

    public override ulong Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) => ReadCore(ref reader);
}
