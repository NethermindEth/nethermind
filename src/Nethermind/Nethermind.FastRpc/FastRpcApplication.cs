// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Nethermind.FastRpc;

/// <summary>
/// Standalone JSON/SSZ REST and JSON-RPC request dispatcher.
/// </summary>
public sealed class FastRpcApplication
{
    private const string BearerPrefix = "Bearer ";
    private const string OctetStream = "application/octet-stream";
    private const int WebSocketBufferSize = 64 * 1024;

    private readonly Dictionary<string, FastRpcHandler> _jsonRpcHandlers;
    private readonly Dictionary<string, FastRpcEndpoint> _restHandlers;
    private readonly FastRpcOptions _options;
    private string? _lastValidAuthorization;

    internal FastRpcApplication(Dictionary<string, FastRpcHandler> handlers, FastRpcOptions options)
    {
        _options = options;
        _jsonRpcHandlers = new Dictionary<string, FastRpcHandler>(handlers, StringComparer.Ordinal);
        _restHandlers = new Dictionary<string, FastRpcEndpoint>(handlers.Count, StringComparer.Ordinal);
        foreach ((string method, FastRpcHandler handler) in handlers)
        {
            _restHandlers[BuildRestPath(options.RestPathPrefix, method)] = new FastRpcEndpoint(method, handler);
        }
    }

    /// <summary>
    /// Processes one ASP.NET Core request.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsAuthorized(context))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        string path = context.Request.Path.Value ?? string.Empty;

        if (context.WebSockets.IsWebSocketRequest
            && string.Equals(path, _options.WebSocketPath, StringComparison.Ordinal))
        {
            await ProcessWebSocketAsync(context);
            return;
        }

        if (HttpMethods.IsPost(context.Request.Method)
            && string.Equals(path, _options.JsonRpcPath, StringComparison.Ordinal))
        {
            ReadOnlyMemory<byte> body = await ReadBodyBytesAsync(context.Request.BodyReader, context.RequestAborted);
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = StatusCodes.Status200OK;
            await WriteJsonRpcAsync(body, context.Response.BodyWriter, context.RequestAborted);
            await context.Response.BodyWriter.FlushAsync(context.RequestAborted);
            return;
        }

        if (TryGetRestEndpoint(context, path, out FastRpcEndpoint endpoint))
        {
            await ProcessRestAsync(context, endpoint, WantsSsz(context));
            return;
        }

        context.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private bool IsAuthorized(HttpContext context)
    {
        if (_options.JwtSecret is null) return true;

        string? authorization = context.Request.Headers.Authorization;
        if (authorization is null || !authorization.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(authorization, _lastValidAuthorization, StringComparison.Ordinal))
        {
            return true;
        }

        bool isValid = FastJwt.ValidateHmacSha256(authorization[BearerPrefix.Length..], _options.JwtSecret);
        if (isValid)
        {
            Volatile.Write(ref _lastValidAuthorization, authorization);
        }

        return isValid;
    }

    private async Task ProcessRestAsync(HttpContext context, FastRpcEndpoint endpoint, bool isSsz)
    {
        ReadOnlyMemory<byte> requestBody = default;
        if (HttpMethods.IsPost(context.Request.Method) && context.Request.ContentLength is > 0)
        {
            if (_options.BufferRestRequestBody)
            {
                requestBody = await ReadBodyBytesAsync(context.Request.BodyReader, context.RequestAborted);
            }
            else
            {
                await DrainBodyAsync(context.Request.BodyReader, context.RequestAborted);
            }
        }

        FastRpcResponse response = await endpoint.Handler(
            new FastRpcRequest(endpoint.Method, hasParams: false, default, requestBody),
            context.RequestAborted);
        ReadOnlyMemory<byte> responseBody = isSsz ? response.Ssz : response.Json;
        if (responseBody.IsEmpty)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = isSsz ? OctetStream : "application/json";
        context.Response.BodyWriter.Write(responseBody.Span);
        await context.Response.BodyWriter.FlushAsync(context.RequestAborted);
    }

    private async Task ProcessWebSocketAsync(HttpContext context)
    {
        using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
        byte[] receiveBuffer = ArrayPool<byte>.Shared.Rent(WebSocketBufferSize);
        ArrayBufferWriter<byte> message = new(WebSocketBufferSize);
        ArrayBufferWriter<byte> response = new(WebSocketBufferSize);

        try
        {
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                ValueWebSocketReceiveResult result =
                    await socket.ReceiveAsync(receiveBuffer.AsMemory(), context.RequestAborted);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", context.RequestAborted);
                    return;
                }

                message.Write(receiveBuffer.AsSpan(0, result.Count));
                if (!result.EndOfMessage) continue;

                await WriteJsonRpcAsync(message.WrittenMemory, response, context.RequestAborted);
                await socket.SendAsync(response.WrittenMemory, WebSocketMessageType.Text, endOfMessage: true, context.RequestAborted);
                message.Clear();
                response.Clear();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(receiveBuffer);
        }
    }

    private async ValueTask WriteJsonRpcAsync(
        ReadOnlyMemory<byte> body,
        IBufferWriter<byte> output,
        CancellationToken cancellationToken)
    {
        using Utf8JsonWriter writer = new(output);

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                {
                    WriteJsonRpcError(writer, id: null, code: -32600, "Empty batch");
                }
                else
                {
                    writer.WriteStartArray();
                    foreach (JsonElement item in root.EnumerateArray())
                    {
                        await WriteJsonRpcResponseAsync(writer, item, cancellationToken);
                    }
                    writer.WriteEndArray();
                }
            }
            else
            {
                await WriteJsonRpcResponseAsync(writer, root, cancellationToken);
            }
        }
        catch (JsonException)
        {
            WriteJsonRpcError(writer, id: null, code: -32700, "Parse error");
        }

        await writer.FlushAsync(cancellationToken);
    }

    private async ValueTask WriteJsonRpcResponseAsync(
        Utf8JsonWriter writer,
        JsonElement request,
        CancellationToken cancellationToken)
    {
        if (request.ValueKind != JsonValueKind.Object
            || !request.TryGetProperty("method", out JsonElement methodElement)
            || methodElement.ValueKind != JsonValueKind.String)
        {
            WriteJsonRpcError(writer, id: null, code: -32600, "Invalid request");
            return;
        }

        string? method = methodElement.GetString();
        if (method is null || !_jsonRpcHandlers.TryGetValue(method, out FastRpcHandler? handler))
        {
            JsonElement? id = request.TryGetProperty("id", out JsonElement missingId) ? missingId : null;
            WriteJsonRpcError(writer, id, code: -32601, "Method not found");
            return;
        }

        bool hasParams = request.TryGetProperty("params", out JsonElement paramsElement);
        FastRpcResponse response = await handler(new FastRpcRequest(method, hasParams, paramsElement), cancellationToken);

        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id");
        if (request.TryGetProperty("id", out JsonElement idElement))
        {
            idElement.WriteTo(writer);
        }
        else
        {
            writer.WriteNullValue();
        }
        writer.WritePropertyName("result");
        writer.WriteRawValue(response.Json.Span, skipInputValidation: true);
        writer.WriteEndObject();
    }

    private static async ValueTask<byte[]> ReadBodyBytesAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        ArrayBufferWriter<byte> writer = new();

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;
            foreach (ReadOnlyMemory<byte> segment in buffer)
            {
                writer.Write(segment.Span);
            }

            reader.AdvanceTo(buffer.End);
            if (result.IsCompleted) break;
        }

        return writer.WrittenMemory.ToArray();
    }

    private async ValueTask DrainBodyAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted) break;
        }
    }

    private bool TryGetRestEndpoint(
        HttpContext context,
        string path,
        [MaybeNullWhen(false)] out FastRpcEndpoint endpoint)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsPost(context.Request.Method))
        {
            endpoint = default;
            return false;
        }

        if (path.Length == 0 || string.Equals(path, _options.JsonRpcPath, StringComparison.Ordinal))
        {
            endpoint = default;
            return false;
        }

        return _restHandlers.TryGetValue(path, out endpoint);
    }

    private static string BuildRestPath(string prefix, string method)
    {
        if (prefix == "/")
        {
            return string.Concat("/", method);
        }

        return prefix.EndsWith("/", StringComparison.Ordinal)
            ? string.Concat(prefix, method)
            : string.Concat(prefix, "/", method);
    }

    private static bool WantsSsz(HttpContext context)
    {
        foreach (string? accept in context.Request.Headers.Accept)
        {
            if (accept?.Contains(OctetStream, StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }
        }

        return context.Request.ContentType?.Contains(OctetStream, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static void WriteJsonRpcError(Utf8JsonWriter writer, JsonElement? id, int code, string message)
    {
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WritePropertyName("id");
        if (id.HasValue)
        {
            id.Value.WriteTo(writer);
        }
        else
        {
            writer.WriteNullValue();
        }
        writer.WriteStartObject("error");
        writer.WriteNumber("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private readonly struct FastRpcEndpoint(string method, FastRpcHandler handler)
    {
        public string Method { get; } = method;
        public FastRpcHandler Handler { get; } = handler;
    }
}
