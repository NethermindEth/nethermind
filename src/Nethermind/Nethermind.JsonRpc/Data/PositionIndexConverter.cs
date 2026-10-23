// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Exceptions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Data;

/// <summary>
/// Reads a position index as the registered converter does, but reports a malformed hex index with a public message.
/// </summary>
internal sealed class PositionIndexConverter : JsonConverter<UInt256>
{
    public override UInt256 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        try
        {
            return GetRegisteredConverter().Read(ref reader, typeToConvert, options);
        }
        catch (FormatException e)
        {
            string reason = e is IExceptionWithSafePublicMessage ? e.Message : "invalid hex string";
            throw new SafePublicMessageFormatException($"json: cannot unmarshal {reason} into Go value of type hexutil.Uint", e);
        }
    }

    public override void Write(Utf8JsonWriter writer, UInt256 value, JsonSerializerOptions options) =>
        GetRegisteredConverter().Write(writer, value, options);

    private static JsonConverter<UInt256> GetRegisteredConverter() =>
        (JsonConverter<UInt256>)TypeInfoJsonSerializer.GetTypeInfo<UInt256>(EthereumJsonSerializer.JsonRpcRequestOptions).Converter;
}
