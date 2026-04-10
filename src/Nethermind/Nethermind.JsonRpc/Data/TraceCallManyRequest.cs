// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core.Collections;

namespace Nethermind.JsonRpc.Data;

[JsonConverter(typeof(TraceCallManyRequestConverter))]
public class TraceCallManyRequest : IJsonRpcParam, IDisposable
{
    private const int MaxCallCount = 1024;

    public ArrayPoolList<TransactionForRpcWithTraceTypes> Calls { get; set; } = null!;

    public void Dispose() => Calls?.Dispose();

    /// <summary>
    /// Used by the JSON-RPC framework to deserialize from a JsonElement.
    /// Validates array length before allocating objects.
    /// </summary>
    public void ReadJson(JsonElement jsonValue, JsonSerializerOptions options)
    {
        JsonDocument? doc = null;
        try
        {
            if (jsonValue.ValueKind == JsonValueKind.String)
            {
                doc = JsonDocument.Parse(jsonValue.GetString()!);
                jsonValue = doc.RootElement;
            }

            if (jsonValue.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Expected an array of calls");
            }

            int count = jsonValue.GetArrayLength();
            if (count > MaxCallCount)
            {
                throw new JsonException($"Too many calls ({count}). Max is {MaxCallCount}.");
            }

            ArrayPoolList<TransactionForRpcWithTraceTypes> calls = new(count);
            try
            {
                foreach (JsonElement element in jsonValue.EnumerateArray())
                {
                    TransactionForRpcWithTraceTypes? call = element.Deserialize<TransactionForRpcWithTraceTypes>(options);
                    if (call is not null)
                    {
                        calls.Add(call);
                    }
                }

                Calls = calls;
            }
            catch
            {
                calls.Dispose();
                throw;
            }
        }
        finally
        {
            doc?.Dispose();
        }
    }

    /// <summary>
    /// Used by JsonSerializer.Deserialize (e.g., in tests).
    /// Streaming deserialization with early termination on limit.
    /// </summary>
    private class TraceCallManyRequestConverter : JsonConverter<TraceCallManyRequest>
    {
        public override TraceCallManyRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                throw new JsonException("Expected an array of calls");
            }

            ArrayPoolList<TransactionForRpcWithTraceTypes> calls = new(4);
            try
            {
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        break;
                    }

                    if (calls.Count >= MaxCallCount)
                    {
                        throw new JsonException($"Too many calls. Max is {MaxCallCount}.");
                    }

                    TransactionForRpcWithTraceTypes? call = JsonSerializer.Deserialize<TransactionForRpcWithTraceTypes>(ref reader, options);
                    if (call is not null)
                    {
                        calls.Add(call);
                    }
                }

                return new TraceCallManyRequest { Calls = calls };
            }
            catch
            {
                calls.Dispose();
                throw;
            }
        }

        public override void Write(Utf8JsonWriter writer, TraceCallManyRequest value, JsonSerializerOptions options)
            => throw new NotSupportedException();
    }
}
