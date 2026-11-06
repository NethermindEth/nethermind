// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nethermind.LightClient;

internal static class RpcEndpoint
{
    internal const int StandardRequestBodySize = 64 * 1024;
    internal const int MaxRequestBodySize = 4 * 1024 * 1024 + StandardRequestBodySize;
    internal const int BatchResponseLimit = 16 * 1024 * 1024;
    internal const int MaxBatchCount = 32;
    internal const int MaxBatchEnvelopes = 128;
    private const int MaxActiveRequests = 64;
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(90);
    private static readonly SemaphoreSlim ActiveRequests = new(MaxActiveRequests);
    private static readonly SemaphoreSlim LargeRequests = new(4, 4);

    internal static async Task HandleAsync(HttpContext context,
        Func<string, JsonElement, CancellationToken, Task<object>> invoke, ILogger logger, TimeProvider? clock = null,
        CancellationToken stoppingToken = default, LightClientMetrics? metrics = null)
    {
        if (!ActiveRequests.Wait(0))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            metrics?.RecordHttp("overloaded");
            logger.LogWarning("RPC request rejected because {Limit} requests are already active", MaxActiveRequests);
            return;
        }

        string outcome = "failed";
        try
        {
            using CancellationTokenSource timeout = new(RequestDeadline, clock ?? TimeProvider.System);
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, context.RequestAborted, stoppingToken);
            try { outcome = await HandleCoreAsync(context, invoke, logger, cancellation.Token, metrics); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
            {
                if (context.Response.HasStarted) context.Abort();
                else context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                outcome = "timed_out";
                logger.LogWarning("RPC request exceeded the {DeadlineSeconds}-second deadline", RequestDeadline.TotalSeconds);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                outcome = "cancelled";
                throw;
            }
        }
        finally
        {
            ActiveRequests.Release();
            metrics?.RecordHttp(outcome);
        }
    }

    private static async Task<string> HandleCoreAsync(HttpContext context,
        Func<string, JsonElement, CancellationToken, Task<object>> invoke, ILogger logger, CancellationToken cancellationToken,
        LightClientMetrics? metrics)
    {
        long started = Stopwatch.GetTimestamp();
        bool large = false;
        try
        {
            using MemoryStream input = new();
            byte[] buffer = new byte[4096];
            try
            {
                while (true)
                {
                    int read = await context.Request.Body.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;
                    if (input.Length + read > MaxRequestBodySize)
                        throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);
                    if (!large && input.Length + read > StandardRequestBodySize)
                    {
                        if (!LargeRequests.Wait(0))
                        {
                            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                            logger.LogWarning("RPC body rejected because four large requests are already active");
                            return "overloaded";
                        }
                        large = true;
                    }
                    input.Write(buffer, 0, read);
                }
            }
            catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                logger.LogInformation("RPC request -> body too large in {ElapsedMs:F1} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return "too_large";
            }
            JsonDocument document;
            try { document = JsonDocument.Parse(input.ToArray()); }
            catch (JsonException)
            {
                logger.LogInformation("RPC request -> parse error in {ElapsedMs:F1} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                await context.Response.WriteAsJsonAsync(Error(null, -32700, "Parse error"), cancellationToken);
                return "parse_error";
            }
            using (document)
            {
                JsonElement request = document.RootElement;
                if (large && !AllowsLargeBody(request))
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    logger.LogInformation("RPC request -> body too large in {ElapsedMs:F1} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    return "too_large";
                }
                if (request.ValueKind == JsonValueKind.Array)
                {
                    int count = request.GetArrayLength();
                    if (count == 0)
                    {
                        logger.LogInformation("RPC batch -> invalid size in {ElapsedMs:F1} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        await context.Response.WriteAsJsonAsync(Error(null, -32600, "Batch must contain at least one request"), cancellationToken);
                        return "invalid_batch";
                    }
                    await WriteBatchAsync(context, request, invoke, logger, cancellationToken, metrics);
                }
                else
                {
                    object? response = await InvokeAsync(request, invoke, logger, cancellationToken, metrics: metrics);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (response is null) context.Response.StatusCode = StatusCodes.Status204NoContent;
                    else await context.Response.WriteAsJsonAsync(response, cancellationToken);
                }
            }
        }
        finally { if (large) LargeRequests.Release(); }
        return "completed";
    }

    internal static async Task WriteBatchAsync(HttpContext context, JsonElement requests,
        Func<string, JsonElement, CancellationToken, Task<object>> invoke, ILogger logger, CancellationToken cancellationToken,
        LightClientMetrics? metrics = null)
    {
        using BoundedJsonBuffer output = new(BatchResponseLimit);
        bool responses = await WriteBatchAsync(output, BatchResponseLimit, requests, invoke, logger, cancellationToken, metrics);
        if (!responses) context.Response.StatusCode = StatusCodes.Status204NoContent;
        else
        {
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.Body.WriteAsync(output.WrittenMemory, cancellationToken);
        }
    }

    private static bool AllowsLargeBody(JsonElement request)
    {
        try
        {
            return request.ValueKind == JsonValueKind.Object && request.TryGetProperty("method", out JsonElement method)
                && method.ValueKind == JsonValueKind.String && (method.ValueEquals("eth_sendRawTransaction")
                    || method.ValueEquals("eth_call") || method.ValueEquals("eth_estimateGas"));
        }
        catch (InvalidOperationException) { return false; }
    }

    internal static async Task<bool> WriteBatchAsync(BoundedJsonBuffer output, int limit, JsonElement requests,
        Func<string, JsonElement, CancellationToken, Task<object>> invoke, ILogger logger, CancellationToken cancellationToken,
        LightClientMetrics? metrics = null)
    {
        output.GetSpan(1)[0] = (byte)'[';
        output.Advance(1);
        int responses = 0;
        int admitted = 0;
        int examined = 0;
        bool exhausted = false;
        string limitMessage = limit == BatchResponseLimit ? BatchLimitMessage
            : "WebSocket batch exceeds the 256 KiB response limit; submit remaining requests separately.";
        byte[] finalError = JsonSerializer.SerializeToUtf8Bytes(Error(null, -32005, limitMessage));
        using JsonElement.ArrayEnumerator items = requests.EnumerateArray();
        while (items.MoveNext())
        {
            JsonElement item = items.Current;
            cancellationToken.ThrowIfCancellationRequested();
            if (examined++ == MaxBatchEnvelopes)
            {
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    JsonElement remaining = items.Current;
                    bool notification = remaining.ValueKind == JsonValueKind.Object
                        && remaining.TryGetProperty("jsonrpc", out JsonElement version) && version.ValueKind == JsonValueKind.String && version.ValueEquals("2.0")
                        && remaining.TryGetProperty("method", out JsonElement method) && method.ValueKind == JsonValueKind.String
                        && !remaining.TryGetProperty("id", out _);
                    if (!notification)
                    {
                        Copy(JsonSerializer.SerializeToUtf8Bytes(Error(null, -32005,
                            "Batch exceeds 128 envelopes; submit remaining requests separately.")));
                        break;
                    }
                } while (items.MoveNext());
                break;
            }
            // Leave a final limit response when a large error tail cannot fit, before executing another item.
            if ((long)output.WrittenMemory.Length + ErrorSizeBound(item) + finalError.Length + 3 > limit)
            {
                Copy(finalError);
                break;
            }
            bool excess = admitted++ >= MaxBatchCount;
            await InvokeAsync(item, exhausted ? Reject : excess ? RejectExcessBatchItem : invoke,
                excess ? NullLogger.Instance : logger, cancellationToken, Append, excess ? null : metrics);

            bool Append(object response, bool success)
            {
                int remaining = limit - finalError.Length - output.WrittenMemory.Length - (responses == 0 ? 0 : 1) - 2;
                using BoundedJsonBuffer encoded = new(Math.Max(1, remaining), 6 * limit);
                bool unchanged = true;
                try
                {
                    using Utf8JsonWriter writer = new(encoded);
                    JsonSerializer.Serialize(writer, response);
                }
                catch (RpcException)
                {
                    exhausted = true;
                    unchanged = false;
                    object? id = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out JsonElement value)
                        && value.ValueKind is JsonValueKind.Null or JsonValueKind.Number or JsonValueKind.String ? value : null;
                    using BoundedJsonBuffer error = new(remaining, 6 * limit);
                    using (Utf8JsonWriter writer = new(error)) JsonSerializer.Serialize(writer, Error(id, -32005, limitMessage));
                    Copy(error.WrittenMemory.Span);
                    return unchanged;
                }
                Copy(encoded.WrittenMemory.Span);
                return unchanged;
            }
        }
        if (requests.GetArrayLength() > MaxBatchCount)
            logger.LogInformation("RPC batch limited to {ExecutedLimit} executions and {EnvelopeLimit} envelopes; received {Count} items",
                MaxBatchCount, MaxBatchEnvelopes, requests.GetArrayLength());
        cancellationToken.ThrowIfCancellationRequested();
        output.GetSpan(1)[0] = (byte)']';
        output.Advance(1);
        return responses != 0;

        Task<object> Reject(string method, JsonElement parameters, CancellationToken token) =>
            Task.FromException<object>(new RpcException(-32005, limitMessage));

        void Copy(ReadOnlySpan<byte> encoded)
        {
            int separator = responses == 0 ? 0 : 1;
            Span<byte> destination = output.GetSpan(encoded.Length + separator);
            if (separator != 0) destination[0] = (byte)',';
            encoded.CopyTo(destination[separator..]);
            output.Advance(encoded.Length + separator);
            responses++;
        }
    }

    private static long ErrorSizeBound(JsonElement item)
    {
        try
        {
            return item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out JsonElement id)
                ? 192L + 6L * id.GetRawText().Length : 192;
        }
        catch (InvalidOperationException) { return 192; }
    }

    private const string BatchLimitMessage = "HTTP batch exceeds the 16 MiB response budget; submit remaining requests separately.";
    internal static Task<object> RejectExcessBatchItem(string method, JsonElement parameters, CancellationToken cancellationToken) =>
        Task.FromException<object>(new RpcException(-32005, "A batch may execute at most 32 requests; submit remaining requests separately."));

    internal static async Task<object?> InvokeAsync(JsonElement request,
        Func<string, JsonElement, CancellationToken, Task<object>> invoke, ILogger logger, CancellationToken cancellationToken,
        Func<object, bool, bool>? writeResponse = null, LightClientMetrics? metrics = null)
    {
        long started = Stopwatch.GetTimestamp();
        string methodName = "<invalid>";
        string outcome = "error -32600";
        try
        {
            if (request.ValueKind != JsonValueKind.Object ||
                !request.TryGetProperty("jsonrpc", out JsonElement version) || version.ValueKind != JsonValueKind.String || !version.ValueEquals("2.0") ||
                !request.TryGetProperty("method", out JsonElement method) || method.ValueKind != JsonValueKind.String)
                return Respond(Error(null, -32600, "Invalid Request"), false);
            methodName = method.GetString()!;
            bool hasId = request.TryGetProperty("id", out JsonElement id);
            if (hasId && id.ValueKind is not (JsonValueKind.Null or JsonValueKind.Number or JsonValueKind.String))
                return Respond(Error(null, -32600, "Invalid Request"), false);
            if (hasId && id.ValueKind == JsonValueKind.String) _ = id.GetString();
            object? responseId = hasId ? id : null;
            try
            {
                request.TryGetProperty("params", out JsonElement parameters);
                cancellationToken.ThrowIfCancellationRequested();
                object result = await invoke(methodName, parameters, cancellationToken);
                outcome = hasId ? "ok" : "notification";
                return hasId ? Respond(new { jsonrpc = "2.0", id = responseId, result }, true) : null;
            }
            catch (RpcException exception)
            {
                outcome = $"error {exception.Code}";
                return hasId ? Respond(Error(responseId, exception.Code, exception.Message, exception.RevertData), false) : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcome = "error -32002";
                return hasId ? Respond(Error(responseId, -32002, "RPC request timed out."), false) : null;
            }
            catch (ExecutionDataUnavailableException exception)
            {
                outcome = "error -32001";
                logger.LogDebug("Authenticated RPC data is unavailable: {Reason}", exception.Message);
                return hasId ? Respond(Error(responseId, -32001, "Requested authenticated data is unavailable."), false) : null;
            }
            catch (InvalidOperationException exception) when (exception is not ObjectDisposedException)
            {
                outcome = "error -32602";
                return hasId ? Respond(Error(responseId, -32602, "Invalid parameters."), false) : null;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                outcome = "error -32000";
                logger.LogWarning(exception, "Unable to produce a verified RPC response");
                return hasId ? Respond(Error(responseId, -32000, "Unable to verify peer data"), false) : null;
            }
        }
        catch (InvalidOperationException exception) when (exception is not ObjectDisposedException)
        {
            outcome = "error -32600";
            return Respond(Error(null, -32600, "Invalid Request"), false);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested) outcome = "cancelled";
            string displayName = methodName.Length <= 64 ? methodName : methodName[..64] + "…";
            displayName = string.Create(displayName.Length, displayName, static (chars, source) =>
            {
                for (int i = 0; i < chars.Length; i++) chars[i] = char.IsControl(source[i]) ? ' ' : source[i];
            });
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            metrics?.RecordRpc(methodName, outcome, elapsed.TotalSeconds);
            logger.LogInformation("RPC {Method} -> {Outcome} in {ElapsedMs:F1} ms", displayName, outcome, elapsed.TotalMilliseconds);
        }

        object Respond(object response, bool success)
        {
            if (writeResponse is not null && !writeResponse(response, success)) outcome = "error -32005";
            return response;
        }
    }

    internal static object Error(object? id, int code, string message) => new { jsonrpc = "2.0", id, error = new { code, message } };
    private static object Error(object? id, int code, string message, string? data) => data is null
        ? Error(id, code, message)
        : new { jsonrpc = "2.0", id, error = new { code, message, data } };
}
