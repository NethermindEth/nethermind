// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Facade.Eth.RpcTransaction;

namespace Nethermind.JsonRpc.Data;

[JsonConverter(typeof(TransactionForRpcWithTraceTypesConverter))]
public class RpcNethermindTransactionWithTraceTypes
{
    public RpcNethermindTransaction Transaction { get; set; }
    public string[] TraceTypes { get; set; }

    private class TransactionForRpcWithTraceTypesConverter : JsonConverter<RpcNethermindTransactionWithTraceTypes>
    {
        public override RpcNethermindTransactionWithTraceTypes? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            RpcNethermindTransactionWithTraceTypes value = new();

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException();
            }

            reader.Read();

            value.Transaction = JsonSerializer.Deserialize<RpcNethermindTransaction>(ref reader, options);
            reader.Read();
            value.TraceTypes = JsonSerializer.Deserialize<string[]>(ref reader, options);

            reader.Read();

            return value;
        }

        public override void Write(Utf8JsonWriter writer, RpcNethermindTransactionWithTraceTypes value, JsonSerializerOptions options)
            => throw new NotSupportedException();
    }
}
