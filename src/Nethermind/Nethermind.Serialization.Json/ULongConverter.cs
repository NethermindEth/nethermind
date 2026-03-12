// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json;

public class ULongConverter : JsonConverter<ulong>
{
    public static ulong FromString(ReadOnlySpan<byte> s) => NumericConverterHelper.ParseULong(s);

    [SkipLocalsInit]
    public override void Write(
        Utf8JsonWriter writer,
        ulong value,
        JsonSerializerOptions options)
    {
        NumericConverterHelper.WriteULong(writer, value);
    }

    internal static ulong ReadCore(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetUInt64();
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return !reader.HasValueSequence
                ? NumericConverterHelper.ParseULong(reader.ValueSpan)
                : NumericConverterHelper.ParseULong(reader.ValueSequence.ToArray());
        }

        ThrowJsonException();
        return default;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowJsonException() => throw new JsonException();
    }

    public override ulong Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        return ReadCore(ref reader);
    }
}
