// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Exceptions;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Data;

/// <summary>
/// Reads a block count as the registered converter does, but reports a value that is neither a hex nor a decimal
/// integer with a public message.
/// </summary>
internal sealed class BlockCountConverter : JsonConverter<ulong>
{
    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return GetRegisteredConverter().Read(ref reader, typeToConvert, options);
        }
        catch (Exception e) when (e is FormatException or JsonException and not IExceptionWithSafePublicMessage
            && reader.TokenType == JsonTokenType.String
            && reader.GetString() is { } value
            && !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new SafePublicMessageFormatException($"invalid hex or decimal integer \"{value}\"");
        }
    }

    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) =>
        GetRegisteredConverter().Write(writer, value, options);

    private static JsonConverter<ulong> GetRegisteredConverter() =>
        (JsonConverter<ulong>)TypeInfoJsonSerializer.GetTypeInfo<ulong>(EthereumJsonSerializer.JsonRpcRequestOptions).Converter;
}
