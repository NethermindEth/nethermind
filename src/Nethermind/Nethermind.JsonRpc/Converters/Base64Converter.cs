// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Buffers.Text;

namespace Nethermind.JsonRpc.Converters;

public class Base64Converter : JsonConverter<byte[]?>
{
    public override byte[]? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
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

        var length = reader.ValueSpan.Length;
        byte[]? bytes;
        if (length != 0)
        {
            bytes = ArrayPool<byte>.Shared.Rent(length);
            reader.ValueSpan.CopyTo(bytes);
        }
        else
        {
            length = checked((int)reader.ValueSequence.Length);
            if (length == 0)
                return null;

            bytes = ArrayPool<byte>.Shared.Rent(length);
            reader.ValueSequence.CopyTo(bytes);
        }

        if (Base64.DecodeFromUtf8InPlace(bytes.AsSpan(0, length), out int written) != OperationStatus.Done)
            throw new JsonException("Unable to decode base64 data");

        var returnVal = new byte[written];
        Array.Copy(bytes, returnVal, written);

        ArrayPool<byte>.Shared.Return(bytes);

        return returnVal;
    }

    [DoesNotReturn]
    [StackTraceHidden]
    internal static void ThrowInvalidOperationException()
    {
        throw new InvalidOperationException();
    }

    public override void Write(Utf8JsonWriter writer, byte[]? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteBase64StringValue(value);
    }
}

