// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Core.Collections;

namespace Nethermind.JsonRpc.Data;

public class TraceCallManyRequest : IJsonRpcParam, IDisposable
{
    private const int MaxCallCount = 1024;

    public ArrayPoolList<TransactionForRpcWithTraceTypes> Calls { get; set; } = null!;

    public void Dispose() => Calls?.Dispose();

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
}
