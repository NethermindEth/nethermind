// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.Serialization.Json;

public class Base64Converter : JsonConverter<byte[]>
{
    public override byte[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        JsonTokenType tokenType = reader.TokenType;

        if (tokenType == JsonTokenType.None || tokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (tokenType != JsonTokenType.String)
        {
            ThrowJsonException();
        }

        return reader.GetBytesFromBase64();
    }

    [DoesNotReturn, StackTraceHidden]
    internal static void ThrowJsonException() => throw new JsonException();

    public override void Write(Utf8JsonWriter writer, byte[] value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteBase64StringValue(value);
    }
}

