// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.DebugModule;

internal sealed class TraceCallTransactionConverter : JsonConverter<TransactionForRpc>
{
    private readonly TransactionForRpc.TransactionJsonConverter _transactionConverter = new();
    private readonly StrictHexByteArrayConverter _dataConverter = new();

    public override TransactionForRpc? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        Utf8JsonReader original = reader;
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        bool hasInput = false;
        bool hasData = false;
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                hasInput |= property.Name.Equals("input", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind != JsonValueKind.Null;
                hasData |= property.Name.Equals("data", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind != JsonValueKind.Null;
            }
        }

        if (!hasInput || !hasData)
            return _transactionConverter.Read(ref original, typeToConvert, options);

        // Geth's TraceCall uses CallDefaults, not setDefaults: input wins, but both aliases must still decode.
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("data", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind != JsonValueKind.Null)
                    {
                        Utf8JsonReader dataReader = new(Encoding.UTF8.GetBytes(property.Value.GetRawText()));
                        dataReader.Read();
                        _dataConverter.Read(ref dataReader, typeof(byte[]), options);
                    }
                }
                else
                {
                    property.WriteTo(writer);
                }
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
