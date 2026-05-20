// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace Nethermind.JsonRpc;

internal ref struct JsonRpcEnvelopeReader
{
    private readonly ReadOnlySpan<byte> _body;

    public JsonRpcEnvelopeReader(ReadOnlySpan<byte> body) => _body = body;

    public bool TryRead(out JsonRpcEnvelope envelope)
    {
        Utf8JsonReader reader = new(_body, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            envelope = default;
            return false;
        }

        string? jsonRpc = null;
        JsonRpcId id = JsonRpcId.Missing;
        string? method = null;
        bool hasParams = false;
        JsonValueKind paramsKind = JsonValueKind.Undefined;
        int paramsStart = 0;
        int paramsLength = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                envelope = new JsonRpcEnvelope(jsonRpc, id, method, hasParams, paramsKind, paramsStart, paramsLength);
                return true;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                ThrowExpectedPropertyName();
            }

            if (reader.ValueTextEquals("jsonrpc"u8))
            {
                ReadValue(ref reader);
                jsonRpc = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals("2.0"u8) ? "2.0" : null;
                SkipValue(ref reader);
                continue;
            }

            if (reader.ValueTextEquals("id"u8))
            {
                ReadValue(ref reader);
                int valueStart = (int)reader.TokenStartIndex;
                id = ReadId(ref reader, _body.Slice(valueStart, (int)reader.BytesConsumed - valueStart));
                continue;
            }

            if (reader.ValueTextEquals("method"u8))
            {
                ReadValue(ref reader);
                method = reader.TokenType == JsonTokenType.String ? KnownRpcMethodNames.Intern(ref reader) : null;
                SkipValue(ref reader);
                continue;
            }

            if (reader.ValueTextEquals("params"u8))
            {
                ReadValue(ref reader);
                hasParams = true;
                paramsKind = GetValueKind(reader.TokenType);
                int valueStart = (int)reader.TokenStartIndex;
                SkipValue(ref reader);
                paramsStart = valueStart;
                paramsLength = (int)reader.BytesConsumed - valueStart;
                continue;
            }

            ReadValue(ref reader);
            SkipValue(ref reader);
        }

        ThrowIncompleteObject();
        envelope = default;
        return false;

        [DoesNotReturn, StackTraceHidden]
        static void ThrowExpectedPropertyName() =>
            throw new JsonException("Expected JSON-RPC object property name.");

        [DoesNotReturn, StackTraceHidden]
        static void ThrowIncompleteObject() =>
            throw new JsonException("Incomplete JSON-RPC object.");
    }

    private static void ReadValue(ref Utf8JsonReader reader)
    {
        if (!reader.Read())
        {
            ThrowExpectedPropertyValue();
        }

        [DoesNotReturn, StackTraceHidden]
        static void ThrowExpectedPropertyValue() =>
            throw new JsonException("Expected JSON-RPC property value.");
    }

    private static JsonRpcId ReadId(ref Utf8JsonReader reader, ReadOnlySpan<byte> rawToken)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => JsonRpcId.Null,
            JsonTokenType.String => JsonRpcId.FromValidatedRawStringToken(rawToken),
            JsonTokenType.Number when reader.TryGetInt64(out long value) => new JsonRpcId(value),
            JsonTokenType.Number when reader.TryGetDecimal(out decimal value) && value.Scale == 0 => new JsonRpcId(value),
            _ => ThrowUnsupportedId()
        };

        [DoesNotReturn, StackTraceHidden]
        static JsonRpcId ThrowUnsupportedId() =>
            throw new JsonException("Unsupported JSON-RPC ID value.");
    }

    private static JsonValueKind GetValueKind(JsonTokenType tokenType) =>
        tokenType switch
        {
            JsonTokenType.StartObject => JsonValueKind.Object,
            JsonTokenType.StartArray => JsonValueKind.Array,
            JsonTokenType.String => JsonValueKind.String,
            JsonTokenType.Number => JsonValueKind.Number,
            JsonTokenType.True => JsonValueKind.True,
            JsonTokenType.False => JsonValueKind.False,
            JsonTokenType.Null => JsonValueKind.Null,
            _ => JsonValueKind.Undefined
        };

    private static void SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }
    }

}
