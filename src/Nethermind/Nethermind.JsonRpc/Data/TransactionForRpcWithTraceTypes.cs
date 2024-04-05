// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Data
{
    using Nethermind.JsonRpc.Modules.Trace;

    [JsonConverter(typeof(TransactionForRpcWithTraceTypesConverter))]
    public class TransactionForRpcWithTraceTypes
    {
        public TransactionForRpc Transaction { get; set; }
        public string[] TraceTypes { get; set; }
    }
}

namespace Nethermind.JsonRpc.Modules.Trace
{
    using Nethermind.JsonRpc.Data;
    public class TransactionForRpcWithTraceTypesConverter : JsonConverter<TransactionForRpcWithTraceTypes>
    {
        public override TransactionForRpcWithTraceTypes? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            TransactionForRpcWithTraceTypes value = new();

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException();
            }
            reader.Read();

            value.Transaction = JsonSerializer.Deserialize<TransactionForRpc>(ref reader, options);
            reader.Read();
            value.TraceTypes = JsonSerializer.Deserialize<string[]>(ref reader, options);

            reader.Read();

            return value;
        }

        public override void Write(Utf8JsonWriter writer, TransactionForRpcWithTraceTypes value, JsonSerializerOptions options)
        {
            throw new NotSupportedException();
        }
    }
}
