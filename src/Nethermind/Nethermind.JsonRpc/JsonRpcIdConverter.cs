// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc;

/// <summary>Serializes and deserializes <see cref="JsonRpcId"/> values.</summary>
public sealed class JsonRpcIdConverter : JsonConverter<JsonRpcId>
{
    /// <inheritdoc/>
    public override JsonRpcId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt64(out long longValue))
            {
                return new JsonRpcId(longValue);
            }

            if (reader.TryGetDecimal(out decimal decimalValue) && decimalValue.Scale == 0)
            {
                return JsonRpcId.FromValidatedRawDecimalToken(GetRawToken(ref reader), decimalValue);
            }

            return ThrowUnsupportedId();
        }

        if (reader.TokenType == JsonTokenType.Null)
        {
            return JsonRpcId.Null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return new JsonRpcId(reader.GetString()!);
        }

        return ThrowUnsupportedId();

        [DoesNotReturn, StackTraceHidden]
        static JsonRpcId ThrowUnsupportedId() =>
            throw new NotSupportedException();
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, JsonRpcId value, JsonSerializerOptions options) =>
        value.WriteTo(writer);

    private static byte[] GetRawToken(ref Utf8JsonReader reader) =>
        reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray();
}
