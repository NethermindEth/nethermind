// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;

namespace Nethermind.JsonRpc;

internal ref struct JsonRpcEnvelopeReader
{
    private readonly ReadOnlySpan<byte> _body;

    public JsonRpcEnvelopeReader(ReadOnlySpan<byte> body) => _body = body;

    /// <summary>The length of the root object, valid after <see cref="TryRead"/> succeeds; bytes after it are not read.</summary>
    public int ObjectLength { get; private set; }

    public static JsonRpcEnvelope Read(JsonElement element, out JsonElement paramsElement)
    {
        string? jsonRpc = null;
        if (element.TryGetProperty("jsonrpc"u8, out JsonElement versionElement))
        {
            jsonRpc = versionElement.ValueKind == JsonValueKind.String && versionElement.ValueEquals("2.0"u8) ? "2.0" : null;
        }

        JsonRpcId id = JsonRpcId.Missing;
        bool hasUnsupportedId = false;
        if (element.TryGetProperty("id"u8, out JsonElement idElement))
        {
            hasUnsupportedId = !TryReadId(idElement, out id);
        }

        string? method = null;
        if (element.TryGetProperty("method"u8, out JsonElement methodElement))
        {
            method = methodElement.ValueKind == JsonValueKind.String ? KnownRpcMethodNames.Intern(methodElement) : null;
        }

        bool hasParams = element.TryGetProperty("params"u8, out paramsElement);
        return new JsonRpcEnvelope(jsonRpc, in id, method, hasParams, hasParams ? paramsElement.ValueKind : JsonValueKind.Undefined, 0, 0, hasUnsupportedId);
    }

    public bool TryRead(out JsonRpcEnvelope envelope)
    {
        Utf8JsonReader reader = new(_body, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            envelope = default;
            return false;
        }

        envelope = ReadObject(ref reader);
        ObjectLength = (int)reader.BytesConsumed;
        return true;
    }

    /// <summary>Reads the envelope of the object <paramref name="reader"/> is on, leaving the reader on the object's end.</summary>
    /// <remarks>
    /// <paramref name="reader"/> must read the body this instance was created over: token positions, and with them
    /// <see cref="JsonRpcEnvelope.ParamsStart"/>, are offsets into it.
    /// </remarks>
    public readonly JsonRpcEnvelope ReadObject(ref Utf8JsonReader reader)
    {
        string? jsonRpc = null;
        JsonRpcId id = JsonRpcId.Missing;
        bool hasUnsupportedId = false;
        string? method = null;
        bool hasParams = false;
        JsonValueKind paramsKind = JsonValueKind.Undefined;
        int paramsStart = 0;
        int paramsLength = 0;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return new JsonRpcEnvelope(jsonRpc, in id, method, hasParams, paramsKind, paramsStart, paramsLength, hasUnsupportedId);
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
                hasUnsupportedId = !TryReadId(ref reader, _body.Slice(valueStart, (int)reader.BytesConsumed - valueStart), out id);
                SkipValue(ref reader);
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
        return default;

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

    /// <returns><c>false</c> when the ID is not a String, Number or Null; <paramref name="id"/> is then <see cref="JsonRpcId.Missing"/>.</returns>
    private static bool TryReadId(ref Utf8JsonReader reader, ReadOnlySpan<byte> rawToken, out JsonRpcId id)
    {
        id = reader.TokenType switch
        {
            JsonTokenType.Null => JsonRpcId.Null,
            JsonTokenType.String => JsonRpcId.FromValidatedRawStringToken(rawToken),
            JsonTokenType.Number when reader.TryGetInt64(out long value) => new JsonRpcId(value),
            JsonTokenType.Number when reader.TryGetDecimal(out decimal value) && value.Scale == 0 => JsonRpcId.FromValidatedRawDecimalToken(rawToken, value),
            JsonTokenType.Number => JsonRpcId.FromValidatedRawNumberToken(rawToken),
            _ => JsonRpcId.Missing
        };
        return reader.TokenType is JsonTokenType.Null or JsonTokenType.String or JsonTokenType.Number;
    }

    /// <inheritdoc cref="TryReadId(ref Utf8JsonReader, ReadOnlySpan{byte}, out JsonRpcId)"/>
    private static bool TryReadId(JsonElement idElement, out JsonRpcId id)
    {
        id = idElement.ValueKind switch
        {
            JsonValueKind.Number when idElement.TryGetInt64(out long idNumber) => new JsonRpcId(idNumber),
            JsonValueKind.Number when idElement.TryGetDecimal(out decimal value) && value.Scale == 0 => JsonRpcId.FromValidatedRawDecimalToken(Encoding.UTF8.GetBytes(idElement.GetRawText()), value),
            JsonValueKind.Number => JsonRpcId.FromValidatedRawNumberToken(Encoding.UTF8.GetBytes(idElement.GetRawText())),
            JsonValueKind.Null => JsonRpcId.Null,
            JsonValueKind.String => new JsonRpcId(idElement.GetString()!),
            _ => JsonRpcId.Missing
        };
        return idElement.ValueKind is JsonValueKind.Null or JsonValueKind.String or JsonValueKind.Number;
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
