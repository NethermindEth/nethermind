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
                SkipComplexValue(ref reader);
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
                method = reader.TokenType == JsonTokenType.String ? InternMethodName(ref reader) : null;
                SkipComplexValue(ref reader);
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

    private static void SkipComplexValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }
    }

    private static void SkipValue(ref Utf8JsonReader reader)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }
    }

    private static string? InternMethodName(ref Utf8JsonReader methodReader)
    {
        switch (methodReader.ValueSpan.Length)
        {
            case 8:
                if (methodReader.ValueTextEquals("eth_call"u8)) return "eth_call";
                break;
            case 11:
                if (methodReader.ValueTextEquals("eth_chainId"u8)) return "eth_chainId";
                break;
            case 17:
                if (methodReader.ValueTextEquals("engine_getBlobsV2"u8)) return "engine_getBlobsV2";
                if (methodReader.ValueTextEquals("engine_getBlobsV1"u8)) return "engine_getBlobsV1";
                if (methodReader.ValueTextEquals("engine_getBlobsV3"u8)) return "engine_getBlobsV3";
                break;
            case 19:
                if (methodReader.ValueTextEquals("engine_newPayloadV4"u8)) return "engine_newPayloadV4";
                if (methodReader.ValueTextEquals("engine_newPayloadV5"u8)) return "engine_newPayloadV5";
                if (methodReader.ValueTextEquals("engine_newPayloadV3"u8)) return "engine_newPayloadV3";
                if (methodReader.ValueTextEquals("engine_newPayloadV2"u8)) return "engine_newPayloadV2";
                if (methodReader.ValueTextEquals("engine_newPayloadV1"u8)) return "engine_newPayloadV1";
                if (methodReader.ValueTextEquals("engine_getPayloadV4"u8)) return "engine_getPayloadV4";
                if (methodReader.ValueTextEquals("engine_getPayloadV5"u8)) return "engine_getPayloadV5";
                if (methodReader.ValueTextEquals("engine_getPayloadV6"u8)) return "engine_getPayloadV6";
                if (methodReader.ValueTextEquals("engine_getPayloadV3"u8)) return "engine_getPayloadV3";
                if (methodReader.ValueTextEquals("engine_getPayloadV2"u8)) return "engine_getPayloadV2";
                if (methodReader.ValueTextEquals("engine_getPayloadV1"u8)) return "engine_getPayloadV1";
                break;
            case 20:
                if (methodReader.ValueTextEquals("eth_getBlockByNumber"u8)) return "eth_getBlockByNumber";
                break;
            case 25:
                if (methodReader.ValueTextEquals("engine_getClientVersionV1"u8)) return "engine_getClientVersionV1";
                break;
            case 26:
                if (methodReader.ValueTextEquals("engine_forkchoiceUpdatedV3"u8)) return "engine_forkchoiceUpdatedV3";
                if (methodReader.ValueTextEquals("engine_forkchoiceUpdatedV4"u8)) return "engine_forkchoiceUpdatedV4";
                if (methodReader.ValueTextEquals("engine_forkchoiceUpdatedV2"u8)) return "engine_forkchoiceUpdatedV2";
                if (methodReader.ValueTextEquals("engine_forkchoiceUpdatedV1"u8)) return "engine_forkchoiceUpdatedV1";
                break;
            case 27:
                if (methodReader.ValueTextEquals("engine_exchangeCapabilities"u8)) return "engine_exchangeCapabilities";
                break;
            case 31:
                if (methodReader.ValueTextEquals("engine_getPayloadBodiesByHashV1"u8)) return "engine_getPayloadBodiesByHashV1";
                if (methodReader.ValueTextEquals("engine_getPayloadBodiesByHashV2"u8)) return "engine_getPayloadBodiesByHashV2";
                break;
            case 32:
                if (methodReader.ValueTextEquals("engine_getPayloadBodiesByRangeV1"u8)) return "engine_getPayloadBodiesByRangeV1";
                if (methodReader.ValueTextEquals("engine_getPayloadBodiesByRangeV2"u8)) return "engine_getPayloadBodiesByRangeV2";
                break;
            case 40:
                if (methodReader.ValueTextEquals("engine_exchangeTransitionConfigurationV1"u8)) return "engine_exchangeTransitionConfigurationV1";
                break;
        }

        return methodReader.GetString();
    }
}
