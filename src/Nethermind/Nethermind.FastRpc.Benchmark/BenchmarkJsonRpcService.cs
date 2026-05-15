// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.JsonRpc;

namespace Nethermind.FastRpc.Benchmark;

internal sealed class BenchmarkJsonRpcService(BenchmarkPayload[] payloads) : IJsonRpcService
{
    private readonly Dictionary<string, BenchmarkPayload> _payloads = BuildPayloads(payloads);

    public Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, JsonRpcContext context)
    {
        if (!_payloads.TryGetValue(request.Method, out BenchmarkPayload? payload))
        {
            return Task.FromResult<JsonRpcResponse>(
                GetErrorResponse(ErrorCodes.MethodNotFound, "Method not found", request.Id, request.Method));
        }

        using JsonDocument document = JsonDocument.Parse(payload.Json);
        JsonRpcSuccessResponse response = new()
        {
            Id = request.Id,
            MethodName = request.Method,
            Result = document.RootElement.Clone(),
        };

        return Task.FromResult<JsonRpcResponse>(response);
    }

    public JsonRpcErrorResponse GetErrorResponse(
        int errorCode,
        string errorMessage,
        object? id = null,
        string? methodName = null) =>
        new()
        {
            Id = id,
            MethodName = methodName ?? string.Empty,
            Error = new Error
            {
                Code = errorCode,
                Message = errorMessage,
            },
        };

    private static Dictionary<string, BenchmarkPayload> BuildPayloads(BenchmarkPayload[] payloads)
    {
        Dictionary<string, BenchmarkPayload> map = new(StringComparer.Ordinal);
        for (int i = 0; i < payloads.Length; i++)
        {
            map[payloads[i].Name] = payloads[i];
        }

        return map;
    }
}
