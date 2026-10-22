// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Exceptions;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Data;

/// <summary>
/// Reads the full-transactions flag as the registered converter does, but reports a value that is not a boolean
/// with a public message.
/// </summary>
internal sealed class FullTransactionsFlagConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return GetRegisteredConverter().Read(ref reader, typeToConvert, options);
        }
        catch (JsonException) when (GetTokenKind(reader.TokenType) is { } kind)
        {
            throw new SafePublicMessageFormatException($"json: cannot unmarshal {kind} into Go value of type bool");
        }
    }

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        GetRegisteredConverter().Write(writer, value, options);

    private static string? GetTokenKind(JsonTokenType tokenType) => tokenType switch
    {
        JsonTokenType.String => "string",
        JsonTokenType.Number => "number",
        JsonTokenType.StartObject => "object",
        JsonTokenType.StartArray => "array",
        _ => null
    };

    private static JsonConverter<bool> GetRegisteredConverter() =>
        (JsonConverter<bool>)TypeInfoJsonSerializer.GetTypeInfo<bool>(EthereumJsonSerializer.JsonRpcRequestOptions).Converter;
}
