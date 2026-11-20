// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Nethermind.JsonRpc;
using Nethermind.LightClient.Rpc;
using Nethermind.Logging;
using Testably.Abstractions;
using RpcTransport = Nethermind.JsonRpc.Modules.RpcEndpoint;

namespace Nethermind.LightClient.Test;

internal static class RpcTestDriver
{
    internal const int StandardRequestBodySize = RpcEndpoint.StandardRequestBodySize;
    internal const int MaxRequestBodySize = RpcEndpoint.MaxRequestBodySize;
    internal const int BatchResponseLimit = RpcEndpoint.BatchResponseLimit;
    internal const int MaxBatchCount = RpcEndpoint.MaxBatchCount;
    internal const int MaxBatchEnvelopes = RpcEndpoint.MaxBatchEnvelopes;
    private const string BatchLimitMessage = RpcEndpoint.BatchLimitMessage;

    internal static Task HandleAsync(HttpContext context,
        Func<string, JsonElement, CancellationToken, Task<object>> invoke, Microsoft.Extensions.Logging.ILogger logger,
        TimeProvider? clock = null, CancellationToken stoppingToken = default, LightClientMetrics? metrics = null) =>
        RpcEndpoint.HandleAsync(context, CreateProcessor(invoke, logger, metrics), logger, clock, stoppingToken, metrics);

    internal static IJsonRpcProcessor CreateProcessor(Func<string, JsonElement, CancellationToken, Task<object>> invoke,
        Microsoft.Extensions.Logging.ILogger logger, LightClientMetrics? metrics = null) =>
        new JsonRpcProcessor(new RpcServiceDiagnostics(new DelegateService(invoke, logger), logger, metrics),
            new JsonRpcConfig { MaxBatchSize = int.MaxValue, BatchProcessingConcurrency = 1, Timeout = 90_000 },
            new RealFileSystem(), NullLogManager.Instance);

    internal static async Task WriteBatchAsync(HttpContext context, JsonElement requests,
        Func<string, JsonElement, CancellationToken, Task<object>> invoke, Microsoft.Extensions.Logging.ILogger logger,
        CancellationToken cancellationToken, LightClientMetrics? metrics = null)
    {
        using RpcResponseBuffer response = new(BatchResponseLimit, BatchLimitMessage);
        await CreateProcessor(invoke, logger, metrics).ProcessAsync(JsonSerializer.SerializeToUtf8Bytes(requests),
            RpcEndpoint.CreateContext(context, cancellationToken), response, new(JsonRpcInputMode.SingleDocument), cancellationToken);
        if (!response.HasResponses) context.Response.StatusCode = StatusCodes.Status204NoContent;
        else
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.Body.WriteAsync(response.WrittenMemory, cancellationToken);
        }
    }

    internal static async Task<object?> InvokeAsync(JsonElement request,
        Func<string, JsonElement, CancellationToken, Task<object>> invoke, Microsoft.Extensions.Logging.ILogger logger,
        CancellationToken cancellationToken, Func<object, bool, bool>? writeResponse = null, LightClientMetrics? metrics = null)
    {
        using RpcResponseBuffer response = new(BatchResponseLimit, BatchLimitMessage);
        await CreateProcessor(invoke, logger, metrics).ProcessAsync(JsonSerializer.SerializeToUtf8Bytes(request),
            new JsonRpcContext(RpcTransport.Http) { RequestAborted = cancellationToken, StrictEnvelope = true, BatchLimits = new(MaxBatchCount, MaxBatchEnvelopes) },
            response, new(JsonRpcInputMode.SingleDocument), cancellationToken);
        if (!response.HasResponses) return null;
        using JsonDocument document = JsonDocument.Parse(response.WrittenMemory);
        JsonElement value = document.RootElement.Clone();
        writeResponse?.Invoke(value, !value.TryGetProperty("error", out _));
        return value;
    }

    internal static JsonElement Error(object? id, int code, string message)
    {
        using RpcResponseBuffer output = new(BatchResponseLimit, BatchLimitMessage);
        JsonRpcErrorResponse response = new()
        {
            Id = id is null ? JsonRpcId.Null : JsonSerializer.Deserialize<JsonRpcId>(JsonSerializer.SerializeToElement(id)),
            Error = new Error { Code = code, Message = message },
        };
        output.WriteSingleAsync(response, RpcReport.Error, CancellationToken.None).GetAwaiter().GetResult();
        using JsonDocument document = JsonDocument.Parse(output.WrittenMemory);
        return document.RootElement.Clone();
    }

    private sealed class DelegateService(Func<string, JsonElement, CancellationToken, Task<object>> invoke,
        Microsoft.Extensions.Logging.ILogger logger) : IJsonRpcService
    {
        public ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, JsonRpcContext context) =>
            SendRequestAsync(request, context, context.RequestAborted);

        public async ValueTask<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, JsonRpcContext context, CancellationToken cancellationToken)
        {
            if (request.JsonRpc != "2.0" || request.Method is null)
                return GetErrorResponse(-32600, "Invalid Request", JsonRpcId.Null);
            try
            {
                ResultWrapper<object> response = ResultWrapper<object>.Success(await invoke(request.Method, request.Params, cancellationToken));
                response.Id = request.Id;
                return response;
            }
            catch (RpcException exception)
            {
                return new JsonRpcErrorResponse
                {
                    Id = request.Id,
                    Error = new Error { Code = exception.Code, Message = exception.Message, Data = exception.RevertData },
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (ExecutionDataUnavailableException)
            {
                return GetErrorResponse(-32001, "Requested authenticated data is unavailable.", request.Id);
            }
            catch (InvalidOperationException exception) when (exception is not ObjectDisposedException)
            {
                return GetErrorResponse(-32602, "Invalid parameters.", request.Id);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Unable to produce a verified RPC response");
                return GetErrorResponse(-32000, "Unable to verify peer data", request.Id);
            }
        }

        public JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, in JsonRpcId id, string? methodName = null) =>
            new() { Id = id, Error = new Error { Code = errorCode, Message = errorMessage } };
        public JsonRpcErrorResponse GetErrorResponse(int errorCode, string errorMessage, string? methodName = null) =>
            GetErrorResponse(errorCode, errorMessage, JsonRpcId.Null, methodName);
    }
}
