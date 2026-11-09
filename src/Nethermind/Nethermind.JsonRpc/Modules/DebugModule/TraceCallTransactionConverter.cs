// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.DebugModule;

internal sealed class TraceCallTransactionConverter : JsonConverter<TransactionForRpc>
{
    private readonly TransactionForRpc.TransactionJsonConverter _transactionConverter = new();
    private static readonly JsonSerializerOptions StrictDataOptions = new(JsonSerializerOptions.Default)
    {
        Converters = { new StrictHexByteArrayConverter() }
    };

    public override TransactionForRpc? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Utf8JsonReader original = reader;
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return _transactionConverter.Read(ref original, typeToConvert, options);

        JsonElement input = default;
        JsonElement data = default;

        // Geth's TraceCall uses CallDefaults, not setDefaults: a non-null effective input wins over data.
        // A final null resets its alias; every occurrence must still decode, even when superseded.
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                bool isInput = property.Name.Equals("input", StringComparison.OrdinalIgnoreCase);
                if (isInput || property.Name.Equals("data", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind != JsonValueKind.Null)
                    {
                        property.Value.Deserialize<byte[]>(StrictDataOptions);
                    }
                    if (isInput) input = property.Value;
                    else data = property.Value;
                }
                else
                {
                    property.WriteTo(writer);
                }
            }
            JsonElement calldata = input.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) ? input : data;
            if (calldata.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
            {
                writer.WritePropertyName("input");
                calldata.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        Utf8JsonReader normalized = new(buffer.WrittenSpan);
        normalized.Read();
        return _transactionConverter.Read(ref normalized, typeToConvert, options);
    }

    public override void Write(Utf8JsonWriter writer, TransactionForRpc value, JsonSerializerOptions options) =>
        _transactionConverter.Write(writer, value, options);
}
