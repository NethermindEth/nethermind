// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Hosting;
using RpcTransport = Nethermind.JsonRpc.Modules.RpcEndpoint;

namespace Nethermind.LightClient.Rpc;

internal static class RpcEndpoint
{
    internal const int StandardRequestBodySize = 64 * 1024;
    internal const int MaxRequestBodySize = 4 * 1024 * 1024 + StandardRequestBodySize;
    internal const int BatchResponseLimit = 16 * 1024 * 1024;
    internal const int MaxBatchCount = 32;
    internal const int MaxBatchEnvelopes = 128;
    internal const string BatchLimitMessage = "HTTP batch exceeds the 16 MiB response budget; submit remaining requests separately.";
    private static readonly SemaphoreSlim ActiveRequests = new(64, 64);
    private static readonly SemaphoreSlim LargeRequests = new(4, 4);

    internal static async Task HandleAsync(HttpContext context, IJsonRpcProcessor processor,
        Microsoft.Extensions.Logging.ILogger logger, TimeProvider? clock = null,
        CancellationToken stoppingToken = default, LightClientMetrics? metrics = null)
    {
        if (!ActiveRequests.Wait(0))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            metrics?.RecordHttp("overloaded");
            logger.LogWarning("RPC request rejected because 64 requests are already active");
            return;
        }
        bool large = false;
        string outcome = "failed";
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(90), clock ?? TimeProvider.System);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, context.RequestAborted, stoppingToken);
        try
        {
            using JsonRpcRequestBody body = new();
            try
            {
                await body.ReadAsync(context, context.Request.ContentLength, MaxRequestBodySize,
                    cancellation.Token, AdmitBody);
            }
            catch (BodyAdmissionException)
            {
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                outcome = "overloaded";
                logger.LogWarning("RPC body rejected because four large requests are already active");
                return;
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                outcome = "too_large";
                logger.LogWarning("RPC body too large; maximum size is {Limit} bytes", MaxRequestBodySize);
                return;
            }
            if (large && !AllowsLargeBody(body.Memory))
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                outcome = "too_large";
                return;
            }
            bool batchInput = body.Memory.Span.TrimStart(" \r\n\t"u8).StartsWith("["u8);
            using RpcResponseBuffer response = new(BatchResponseLimit, BatchLimitMessage, batchInput: batchInput);
            JsonRpcContext request = CreateContext(context, cancellation.Token);
            await processor.ProcessAsync(body.Memory, request, response,
                new(JsonRpcInputMode.SingleDocument), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!response.HasResponses) context.Response.StatusCode = StatusCodes.Status204NoContent;
            else
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.Body.WriteAsync(response.WrittenMemory, cancellation.Token);
            }
            outcome = response.ErrorCode == ErrorCodes.ParseError ? "parse_error"
                : response.ErrorCode == ErrorCodes.InvalidRequest && batchInput
                    ? "invalid_batch" : "completed";
        }
        catch (BadHttpRequestException exception)
        {
            context.Response.StatusCode = exception.StatusCode;
            outcome = "invalid_body";
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        {
            if (context.Response.HasStarted) context.Abort();
            else context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
            outcome = "timed_out";
            logger.LogWarning("RPC request exceeded the 90-second deadline");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            if (large) LargeRequests.Release();
            ActiveRequests.Release();
            metrics?.RecordHttp(outcome);
        }

        void AdmitBody(int bytes)
        {
            if (!large && bytes > StandardRequestBodySize)
            {
                if (!LargeRequests.Wait(0)) throw new BodyAdmissionException();
                large = true;
            }
        }
    }

    internal static JsonRpcContext CreateContext(HttpContext context, CancellationToken cancellationToken) => new(RpcTransport.Http)
    {
        RequestAborted = cancellationToken,
        StrictEnvelope = true,
        BatchLimits = new(MaxBatchCount, MaxBatchEnvelopes),
        RemoteAddress = context.Connection.RemoteIpAddress,
        ForwardedFor = context.Request.Headers["X-Forwarded-For"],
    };

    private static bool AllowsLargeBody(ReadOnlyMemory<byte> body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement request = document.RootElement;
            return request.ValueKind == JsonValueKind.Object && request.TryGetProperty("method", out JsonElement method)
                && method.ValueKind == JsonValueKind.String && (method.ValueEquals("eth_sendRawTransaction")
                    || method.ValueEquals("eth_call") || method.ValueEquals("eth_estimateGas"));
        }
        catch (JsonException) { return true; }
        catch (InvalidOperationException) { return false; }
    }

    private sealed class BodyAdmissionException : Exception { }
}
