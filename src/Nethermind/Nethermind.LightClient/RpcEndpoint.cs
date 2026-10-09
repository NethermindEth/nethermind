// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text.Json;

namespace Nethermind.LightClient;

internal static class RpcEndpoint
{
    private const int MaxActiveRequests = 64;
    private static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(90);
    private static readonly SemaphoreSlim ActiveRequests = new(MaxActiveRequests);

    internal static async Task HandleAsync(HttpContext context, VerifiedRpc rpc, ILogger logger, TimeProvider? clock = null)
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
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, context.RequestAborted);
            try { await HandleCoreAsync(context, rpc, logger, cancellation.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
            {
                if (context.Response.HasStarted) context.Abort();
                else context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                logger.LogWarning("RPC request exceeded the {DeadlineSeconds}-second deadline", RequestDeadline.TotalSeconds);
            }
        }
        finally { ActiveRequests.Release(); }
    }

    private static async Task HandleCoreAsync(HttpContext context, VerifiedRpc rpc, ILogger logger, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        using MemoryStream input = new();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int read = await context.Request.Body.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (input.Length + read > 64 * 1024)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                logger.LogInformation("RPC request -> body too large in {ElapsedMs:F1} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                return;
            }
            input.Write(buffer, 0, read);
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
            if (request.ValueKind == JsonValueKind.Array)
            {
                int count = request.GetArrayLength();
                if (count is 0 or > 32)
                {
                    logger.LogInformation("RPC batch -> invalid size in {ElapsedMs:F1} ms", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    await context.Response.WriteAsJsonAsync(Error(null, -32600, "Batch must contain between 1 and 32 requests"), cancellationToken);
                    return;
                }
                List<object> responses = new(count);
                foreach (JsonElement item in request.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    object? response = await InvokeAsync(item, rpc, logger, cancellationToken);
                    if (response is not null) responses.Add(response);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (responses.Count == 0) context.Response.StatusCode = StatusCodes.Status204NoContent;
                else await context.Response.WriteAsJsonAsync(responses, cancellationToken);
            }
            else
            {
                object? response = await InvokeAsync(request, rpc, logger, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (response is null) context.Response.StatusCode = StatusCodes.Status204NoContent;
                else await context.Response.WriteAsJsonAsync(response, cancellationToken);
            }
        }
    }

    private static async Task<object?> InvokeAsync(JsonElement request, VerifiedRpc rpc, ILogger logger, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        string methodName = "<invalid>";
        string outcome = "error -32600";
        try
        {
            if (request.ValueKind != JsonValueKind.Object ||
                !request.TryGetProperty("jsonrpc", out JsonElement version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
                !request.TryGetProperty("method", out JsonElement method) || method.ValueKind != JsonValueKind.String)
                return Error(null, -32600, "Invalid Request");
            methodName = method.GetString()!;
            bool hasId = request.TryGetProperty("id", out JsonElement id);
            if (hasId && id.ValueKind is not (JsonValueKind.Null or JsonValueKind.Number or JsonValueKind.String))
                return Error(null, -32600, "Invalid Request");
            object? responseId = hasId ? id : null;
            try
            {
                request.TryGetProperty("params", out JsonElement parameters);
                object result = await rpc.InvokeAsync(methodName, parameters, cancellationToken);
                outcome = hasId ? "ok" : "notification";
                return hasId ? new { jsonrpc = "2.0", id = responseId, result } : null;
            }
            catch (RpcException exception)
            {
                outcome = $"error {exception.Code}";
                return hasId ? Error(responseId, exception.Code, exception.Message, exception.RevertData) : null;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                outcome = "error -32000";
                logger.LogWarning(exception, "Unable to produce a verified RPC response");
                return hasId ? Error(responseId, -32000, "Unable to verify peer data") : null;
            }
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested) outcome = "cancelled";
            string displayName = (methodName.Length <= 64 ? methodName : methodName[..64] + "…")
                .Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            logger.LogInformation("RPC {Method} -> {Outcome} in {ElapsedMs:F1} ms", displayName, outcome, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private static object Error(object? id, int code, string message) => new { jsonrpc = "2.0", id, error = new { code, message } };
    private static object Error(object? id, int code, string message, string? data) => data is null
        ? Error(id, code, message)
        : new { jsonrpc = "2.0", id, error = new { code, message, data } };
}
