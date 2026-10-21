// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text.Json;

namespace Nethermind.LightClient;

internal static class RpcEndpoint
{
    internal const int StandardRequestBodySize = 64 * 1024;
    internal const int MaxRequestBodySize = 4 * 1024 * 1024 + StandardRequestBodySize;
    internal const int BatchResponseLimit = 16 * 1024 * 1024;
    internal const int BatchErrorReserve = 512 * 1024;
    private const int MaxActiveRequests = 64;
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(90);
    private static readonly SemaphoreSlim ActiveRequests = new(MaxActiveRequests);
    private static readonly SemaphoreSlim LargeRequests = new(4, 4);

    internal static async Task HandleAsync(HttpContext context,
        Func<string, JsonElement, CancellationToken, Task<object>> invoke, ILogger logger, TimeProvider? clock = null,
        CancellationToken stoppingToken = default)
    {
        if (!ActiveRequests.Wait(0))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            logger.LogWarning("RPC request rejected because {Limit} requests are already active", MaxActiveRequests);
            return;
        }

        try
        {
            using CancellationTokenSource timeout = new(RequestDeadline, clock ?? TimeProvider.System);
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, context.RequestAborted, stoppingToken);
            try { await HandleCoreAsync(context, invoke, logger, cancellation.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
            {
                if (context.Response.HasStarted) context.Abort();
                else context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                logger.LogWarning("RPC request exceeded the {DeadlineSeconds}-second deadline", RequestDeadline.TotalSeconds);
            }
        }
        finally { ActiveRequests.Release(); }
    }

    private static async Task HandleCoreAsync(HttpContext context,
        Func<string, JsonElement, CancellationToken, Task<object>> invoke, ILogger logger, CancellationToken cancellationToken)
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
                            logger.LogWarning("RPC body rejected because four large transaction submissions are already active");
                            return;
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
                return;
            }
            JsonDocument document;
            try { document = JsonDocument.Parse(input.ToArray()); }
            catch (JsonException)
            {
                logger.LogInformation("RPC request -> parse error in {ElapsedMs:F1} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                await context.Response.WriteAsJsonAsync(Error(null, -32700, "Parse error"), cancellationToken);
                return;
            }
            using (document)
            {
                JsonElement request = document.RootElement;
                if (large && (request.ValueKind != JsonValueKind.Object || !request.TryGetProperty("method", out JsonElement largeMethod)
                    || largeMethod.ValueKind != JsonValueKind.String || largeMethod.GetString() != "eth_sendRawTransaction"))
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    logger.LogInformation("RPC request -> body too large in {ElapsedMs:F1} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    return;
                }
                if (request.ValueKind == JsonValueKind.Array)
                {
                    int count = request.GetArrayLength();
                    if (count is 0 or > 32)
                    {
                        logger.LogInformation("RPC batch -> invalid size in {ElapsedMs:F1} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        await context.Response.WriteAsJsonAsync(Error(null, -32600, "Batch must contain between 1 and 32 requests"), cancellationToken);
                        return;
                    }
                    await WriteBatchAsync(context, request, invoke, logger, cancellationToken);
                }
                else
                {
                    object? response = await InvokeAsync(request, invoke, logger, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (response is null) context.Response.StatusCode = StatusCodes.Status204NoContent;
                    else await context.Response.WriteAsJsonAsync(response, cancellationToken);
                }
            }
        }
        finally { if (large) LargeRequests.Release(); }
    }

    internal static async Task WriteBatchAsync(HttpContext context, JsonElement requests,
        Func<string, JsonElement, CancellationToken, Task<object>> invoke, ILogger logger, CancellationToken cancellationToken)
    {
        using BoundedJsonBuffer output = new(BatchResponseLimit);
        output.GetSpan(1)[0] = (byte)'[';
        output.Advance(1);
        int responses = 0;
        bool exhausted = false;
        foreach (JsonElement item in requests.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await InvokeAsync(item, exhausted ? RejectBatchItem : invoke, logger, cancellationToken, Append);

            bool Append(object response, bool success)
            {
                int remaining = BatchResponseLimit - BatchErrorReserve - output.WrittenMemory.Length - (responses == 0 ? 0 : 1) - 1;
                using BoundedJsonBuffer encoded = new(success ? Math.Max(1, remaining) : BatchErrorReserve);
                bool unchanged = true;
                try
                {
                    using (Utf8JsonWriter writer = new(encoded)) JsonSerializer.Serialize(writer, response);
                    if (encoded.WrittenMemory.Length > remaining)
                    {
                        exhausted = true;
                        if (success) throw BatchTooLarge();
                    }
                }
                catch (RpcException)
                {
                    exhausted = true;
                    unchanged = false;
                    object? id = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out JsonElement value)
                        && value.ValueKind is JsonValueKind.Null or JsonValueKind.Number or JsonValueKind.String ? value : null;
                    using BoundedJsonBuffer error = new(BatchErrorReserve);
                    using (Utf8JsonWriter writer = new(error)) JsonSerializer.Serialize(writer, Error(id, -32005, BatchLimitMessage));
                    Copy(error.WrittenMemory.Span);
                    return unchanged;
                }
                Copy(encoded.WrittenMemory.Span);
                return unchanged;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (responses == 0) context.Response.StatusCode = StatusCodes.Status204NoContent;
        else
        {
            output.GetSpan(1)[0] = (byte)']';
            output.Advance(1);
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.Body.WriteAsync(output.WrittenMemory, cancellationToken);
        }

        void Copy(ReadOnlySpan<byte> encoded)
        {
            int separator = responses == 0 ? 0 : 1;
            Span<byte> destination = output.GetSpan(encoded.Length + separator);
            if (separator != 0) destination[0] = (byte)',';
            encoded.CopyTo(destination[separator..]);
            output.Advance(encoded.Length + separator);
            responses++;
            if (output.WrittenMemory.Length + 1 >= BatchResponseLimit - BatchErrorReserve) exhausted = true;
        }
    }

    private const string BatchLimitMessage = "HTTP batch exceeds the 16 MiB response budget; submit remaining requests separately.";
    private static RpcException BatchTooLarge() => new(-32005, BatchLimitMessage);
    private static Task<object> RejectBatchItem(string method, JsonElement parameters, CancellationToken cancellationToken) =>
        Task.FromException<object>(BatchTooLarge());

    internal static async Task<object?> InvokeAsync(JsonElement request,
        Func<string, JsonElement, CancellationToken, Task<object>> invoke, ILogger logger, CancellationToken cancellationToken,
        Func<object, bool, bool>? writeResponse = null)
    {
        long started = Stopwatch.GetTimestamp();
        string methodName = "<invalid>";
        string outcome = "error -32600";
        try
        {
            if (request.ValueKind != JsonValueKind.Object ||
                !request.TryGetProperty("jsonrpc", out JsonElement version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
                !request.TryGetProperty("method", out JsonElement method) || method.ValueKind != JsonValueKind.String)
                return Respond(Error(null, -32600, "Invalid Request"), false);
            methodName = method.GetString()!;
            bool hasId = request.TryGetProperty("id", out JsonElement id);
            if (hasId && id.ValueKind is not (JsonValueKind.Null or JsonValueKind.Number or JsonValueKind.String))
                return Respond(Error(null, -32600, "Invalid Request"), false);
            object? responseId = hasId ? id : null;
            try
            {
                request.TryGetProperty("params", out JsonElement parameters);
                object result = await invoke(methodName, parameters, cancellationToken);
                outcome = hasId ? "ok" : "notification";
                return hasId ? Respond(new { jsonrpc = "2.0", id = responseId, result }, true) : null;
            }
            catch (RpcException exception)
            {
                outcome = $"error {exception.Code}";
                return hasId ? Respond(Error(responseId, exception.Code, exception.Message, exception.RevertData), false) : null;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                outcome = "error -32000";
                logger.LogWarning(exception, "Unable to produce a verified RPC response");
                return hasId ? Respond(Error(responseId, -32000, "Unable to verify peer data"), false) : null;
            }
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested) outcome = "cancelled";
            string displayName = methodName.Length <= 64 ? methodName : methodName[..64] + "…";
            displayName = string.Create(displayName.Length, displayName, static (chars, source) =>
            {
                for (int i = 0; i < chars.Length; i++) chars[i] = char.IsControl(source[i]) ? ' ' : source[i];
            });
            logger.LogInformation("RPC {Method} -> {Outcome} in {ElapsedMs:F1} ms", displayName, outcome, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        object Respond(object response, bool success)
        {
            if (writeResponse is not null && !writeResponse(response, success)) outcome = "error -32005";
            return response;
        }
    }

    private static object Error(object? id, int code, string message) => new { jsonrpc = "2.0", id, error = new { code, message } };
    private static object Error(object? id, int code, string message, string? data) => data is null
        ? Error(id, code, message)
        : new { jsonrpc = "2.0", id, error = new { code, message, data } };
}
