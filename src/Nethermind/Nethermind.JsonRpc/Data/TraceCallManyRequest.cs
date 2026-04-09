// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;

namespace Nethermind.JsonRpc.Data;

[JsonConverter(typeof(TraceCallManyRequestConverter))]
public class TraceCallManyRequest : IDisposable
{
    public ArrayPoolList<TransactionForRpcWithTraceTypes> Calls { get; init; } = new(0);

    public void Dispose() => Calls.Dispose();

    private class TraceCallManyRequestConverter : JsonConverter<TraceCallManyRequest>
    {
        // Hard safety cap to prevent excessive deserialization regardless of config
        private const int MaxCallCount = 1024;

        public override TraceCallManyRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("Expected an array of calls");
            }

            ArrayPoolList<TransactionForRpcWithTraceTypes> calls = new(4);

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    break;
                }

                if (calls.Count >= MaxCallCount)
                {
                    calls.Dispose();
                    throw new JsonException($"Too many calls. Hard limit is {MaxCallCount}.");
                }

                TransactionForRpcWithTraceTypes? call = JsonSerializer.Deserialize<TransactionForRpcWithTraceTypes>(ref reader, options);
                if (call is not null)
                {
                    calls.Add(call);
                }
            }

            return new TraceCallManyRequest { Calls = calls };
        }

        public override void Write(Utf8JsonWriter writer, TraceCallManyRequest value, JsonSerializerOptions options)
            => throw new NotSupportedException();
    }
}
