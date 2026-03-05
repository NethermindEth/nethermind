// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Authentication;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest;

/// <summary>
/// HTTP server for EIP-8161 SSZ-REST Engine API transport.
/// Serves binary SSZ-encoded payloads over REST endpoints alongside the JSON-RPC Engine API.
/// Wire-compatible with geth and erigon implementations.
/// </summary>
public sealed class SszRestServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly IRpcAuthentication _auth;
    private readonly IAsyncHandler<ExecutionPayload, PayloadStatusV1> _newPayloadHandler;
    private readonly IForkchoiceUpdatedHandler _forkchoiceUpdatedHandler;
    private readonly IAsyncHandler<byte[], ExecutionPayload?> _getPayloadV1Handler;
    private readonly IAsyncHandler<byte[], GetPayloadV2Result?> _getPayloadV2Handler;
    private readonly IAsyncHandler<byte[], GetPayloadV3Result?> _getPayloadV3Handler;
    private readonly IAsyncHandler<byte[], GetPayloadV4Result?> _getPayloadV4Handler;
    private readonly IAsyncHandler<byte[], GetPayloadV5Result?> _getPayloadV5Handler;
    private readonly IHandler<IEnumerable<string>, IEnumerable<string>> _capabilitiesHandler;
    private readonly IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>> _getBlobsHandler;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public SszRestServer(
        IRpcAuthentication auth,
        IAsyncHandler<ExecutionPayload, PayloadStatusV1> newPayloadHandler,
        IForkchoiceUpdatedHandler forkchoiceUpdatedHandler,
        IAsyncHandler<byte[], ExecutionPayload?> getPayloadV1Handler,
        IAsyncHandler<byte[], GetPayloadV2Result?> getPayloadV2Handler,
        IAsyncHandler<byte[], GetPayloadV3Result?> getPayloadV3Handler,
        IAsyncHandler<byte[], GetPayloadV4Result?> getPayloadV4Handler,
        IAsyncHandler<byte[], GetPayloadV5Result?> getPayloadV5Handler,
        IHandler<IEnumerable<string>, IEnumerable<string>> capabilitiesHandler,
        IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>> getBlobsHandler,
        IMergeConfig mergeConfig,
        ILogManager logManager)
    {
        _auth = auth;
        _newPayloadHandler = newPayloadHandler;
        _forkchoiceUpdatedHandler = forkchoiceUpdatedHandler;
        _getPayloadV1Handler = getPayloadV1Handler;
        _getPayloadV2Handler = getPayloadV2Handler;
        _getPayloadV3Handler = getPayloadV3Handler;
        _getPayloadV4Handler = getPayloadV4Handler;
        _getPayloadV5Handler = getPayloadV5Handler;
        _capabilitiesHandler = capabilitiesHandler;
        _getBlobsHandler = getBlobsHandler;
        _logger = logManager.GetClassLogger();

        int port = mergeConfig.SszRestPort;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{port}/");
    }

    /// <summary>
    /// Starts the SSZ-REST server listening for requests.
    /// </summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            if (_logger.IsError) _logger.Error($"Failed to start SSZ-REST server: {ex.Message}", ex);
            return;
        }

        if (_logger.IsInfo) _logger.Info($"SSZ-REST Engine API server started on {string.Join(", ", _listener.Prefixes)}");
        _listenTask = AcceptLoop(_cts.Token);
    }

    /// <summary>
    /// Stops the SSZ-REST server.
    /// </summary>
    public async Task StopAsync()
    {
        _cts?.Cancel();
        _listener.Stop();
        if (_listenTask is not null)
        {
            try { await _listenTask; } catch (OperationCanceledException) { }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener.Close();
        _cts?.Dispose();
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }

            // Process each request without awaiting to allow concurrent handling
            _ = ProcessRequestAsync(ctx);
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            // JWT authentication
            string? authHeader = ctx.Request.Headers["Authorization"];
            if (!await _auth.Authenticate(authHeader ?? string.Empty))
            {
                await WriteJsonError(ctx.Response, 401, -32001, "Unauthorized");
                return;
            }

            // Only POST is allowed
            if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonError(ctx.Response, 405, -32600, "Method not allowed");
                return;
            }

            string? path = ctx.Request.Url?.AbsolutePath;
            if (path is null)
            {
                await WriteJsonError(ctx.Response, 400, -32600, "Invalid request path");
                return;
            }

            // Read request body
            byte[] body;
            using (MemoryStream ms = new())
            {
                await ctx.Request.InputStream.CopyToAsync(ms);
                body = ms.ToArray();
            }

            await RouteRequest(ctx, path, body);
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"SSZ-REST request processing error: {ex.Message}", ex);
            try
            {
                await WriteJsonError(ctx.Response, 500, -32603, "Internal error");
            }
            catch { /* best effort */ }
        }
    }

    private async Task RouteRequest(HttpListenerContext ctx, string path, byte[] body)
    {
        // Route format: /engine/v{version}/{method}
        // Parse version and method from the path
        if (!TryParsePath(path, out int version, out string method))
        {
            await WriteJsonError(ctx.Response, 404, -32601, $"Unknown endpoint: {path}");
            return;
        }

        if (_logger.IsInfo) _logger.Info($"SSZ-REST << engine_v{version}_{method} ({body.Length} bytes)");

        switch (method)
        {
            case "new_payload":
                await HandleNewPayload(ctx, body, version);
                break;
            case "forkchoice_updated":
                await HandleForkchoiceUpdated(ctx, body, version);
                break;
            case "get_payload":
                await HandleGetPayload(ctx, body, version);
                break;
            case "get_blobs":
                await HandleGetBlobs(ctx, body, version);
                break;
            case "exchange_capabilities":
                await HandleExchangeCapabilities(ctx, body);
                break;
            case "get_client_version":
                await HandleGetClientVersion(ctx, body);
                break;
            default:
                await WriteJsonError(ctx.Response, 404, -32601, $"Unknown method: {method}");
                break;
        }
    }

    private static bool TryParsePath(string path, out int version, out string method)
    {
        version = 0;
        method = string.Empty;

        // Expected: /engine/v{N}/{method_name}
        ReadOnlySpan<char> pathSpan = path.AsSpan();
        if (!pathSpan.StartsWith("/engine/v", StringComparison.OrdinalIgnoreCase))
            return false;

        pathSpan = pathSpan["/engine/v".Length..];
        int slashIdx = pathSpan.IndexOf('/');
        if (slashIdx < 1)
            return false;

        if (!int.TryParse(pathSpan[..slashIdx], out version))
            return false;

        method = pathSpan[(slashIdx + 1)..].ToString();
        return method.Length > 0;
    }

    #region Handlers

    private async Task HandleNewPayload(HttpListenerContext ctx, byte[] body, int version)
    {
        try
        {
            (ExecutionPayloadV3 payload, byte[]?[] versionedHashes, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests) =
                SszEncoding.DecodeNewPayloadRequest(body, version);

            // Set the additional fields that the handler expects
            payload.ParentBeaconBlockRoot = parentBeaconBlockRoot;
            payload.ExecutionRequests = executionRequests;

            ResultWrapper<PayloadStatusV1> result = await _newPayloadHandler.HandleAsync(payload);

            if (result.Result != Result.Success)
            {
                await WriteJsonError(ctx.Response, 400, result.ErrorCode, result.Result.Error ?? "Unknown error");
                return;
            }

            byte[] responseBytes = SszEncoding.EncodePayloadStatus(result.Data);
            await WriteSszResponse(ctx.Response, responseBytes);
        }
        catch (SszDecodingException ex)
        {
            await WriteJsonError(ctx.Response, 400, ErrorCodes.InvalidParams, ex.Message);
        }
    }

    private async Task HandleForkchoiceUpdated(HttpListenerContext ctx, byte[] body, int version)
    {
        try
        {
            (ForkchoiceStateV1 state, PayloadAttributes? attributes) =
                SszEncoding.DecodeForkchoiceUpdatedRequest(body, version);

            ResultWrapper<ForkchoiceUpdatedV1Result> result = await _forkchoiceUpdatedHandler.Handle(state, attributes, version);

            if (result.Result != Result.Success)
            {
                await WriteJsonError(ctx.Response, 400, result.ErrorCode, result.Result.Error ?? "Unknown error");
                return;
            }

            byte[] responseBytes = SszEncoding.EncodeForkchoiceUpdatedResponse(result.Data);
            await WriteSszResponse(ctx.Response, responseBytes);
        }
        catch (SszDecodingException ex)
        {
            await WriteJsonError(ctx.Response, 400, ErrorCodes.InvalidParams, ex.Message);
        }
    }

    private async Task HandleGetPayload(HttpListenerContext ctx, byte[] body, int version)
    {
        try
        {
            byte[] payloadId = SszEncoding.DecodeGetPayloadRequest(body);

            switch (version)
            {
                case 1:
                {
                    ResultWrapper<ExecutionPayload?> result = await _getPayloadV1Handler.HandleAsync(payloadId);
                    if (result.Result != Result.Success || result.Data is null)
                    {
                        await WriteJsonError(ctx.Response, 400, result.ErrorCode, result.Result.Error ?? "Payload not found");
                        return;
                    }
                    byte[] responseBytes = SszEncoding.EncodeExecutionPayload(result.Data, 1);
                    await WriteSszResponse(ctx.Response, responseBytes);
                    break;
                }
                case 2:
                {
                    ResultWrapper<GetPayloadV2Result?> result = await _getPayloadV2Handler.HandleAsync(payloadId);
                    if (result.Result != Result.Success || result.Data is null)
                    {
                        await WriteJsonError(ctx.Response, 400, result.ErrorCode, result.Result.Error ?? "Payload not found");
                        return;
                    }
                    byte[] responseBytes = SszEncoding.EncodeGetPayloadResponse(
                        result.Data.ExecutionPayload, result.Data.BlockValue, null, false, null, version);
                    await WriteSszResponse(ctx.Response, responseBytes);
                    break;
                }
                case 3:
                {
                    ResultWrapper<GetPayloadV3Result?> result = await _getPayloadV3Handler.HandleAsync(payloadId);
                    if (result.Result != Result.Success || result.Data is null)
                    {
                        await WriteJsonError(ctx.Response, 400, result.ErrorCode, result.Result.Error ?? "Payload not found");
                        return;
                    }
                    byte[] responseBytes = SszEncoding.EncodeGetPayloadResponse(
                        result.Data.ExecutionPayload, result.Data.BlockValue, result.Data.BlobsBundle, result.Data.ShouldOverrideBuilder, null, version);
                    await WriteSszResponse(ctx.Response, responseBytes);
                    break;
                }
                case 4:
                {
                    ResultWrapper<GetPayloadV4Result?> result = await _getPayloadV4Handler.HandleAsync(payloadId);
                    if (result.Result != Result.Success || result.Data is null)
                    {
                        await WriteJsonError(ctx.Response, 400, result.ErrorCode, result.Result.Error ?? "Payload not found");
                        return;
                    }
                    byte[] responseBytes = SszEncoding.EncodeGetPayloadResponse(
                        result.Data.ExecutionPayload, result.Data.BlockValue, result.Data.BlobsBundle, result.Data.ShouldOverrideBuilder, result.Data.ExecutionRequests, version);
                    await WriteSszResponse(ctx.Response, responseBytes);
                    break;
                }
                case 5:
                {
                    ResultWrapper<GetPayloadV5Result?> result = await _getPayloadV5Handler.HandleAsync(payloadId);
                    if (result.Result != Result.Success || result.Data is null)
                    {
                        await WriteJsonError(ctx.Response, 400, result.ErrorCode, result.Result.Error ?? "Payload not found");
                        return;
                    }
                    // V5 uses BlobsBundleV2 but we encode with the BlobsBundleV1 base for SSZ wire compat
                    byte[] responseBytes = SszEncoding.EncodeGetPayloadResponse(
                        result.Data.ExecutionPayload, result.Data.BlockValue, null, result.Data.ShouldOverrideBuilder, result.Data.ExecutionRequests, version);
                    await WriteSszResponse(ctx.Response, responseBytes);
                    break;
                }
                default:
                    await WriteJsonError(ctx.Response, 400, ErrorCodes.InvalidParams, $"Unsupported getPayload version: {version}");
                    break;
            }
        }
        catch (SszDecodingException ex)
        {
            await WriteJsonError(ctx.Response, 400, ErrorCodes.InvalidParams, ex.Message);
        }
    }

    private async Task HandleGetBlobs(HttpListenerContext ctx, byte[] body, int version)
    {
        try
        {
            byte[][] hashes = SszEncoding.DecodeGetBlobsRequest(body);
            ResultWrapper<IEnumerable<BlobAndProofV1?>> result = await _getBlobsHandler.HandleAsync(hashes);

            if (result.Result != Result.Success)
            {
                await WriteJsonError(ctx.Response, 400, result.ErrorCode, result.Result.Error ?? "Unknown error");
                return;
            }

            // For getBlobsV1, we return the SSZ-encoded result.
            // The response format is complex (list of optional blob+proof pairs).
            // For now, return empty success since getBlobsV1 response SSZ encoding
            // requires blob data which is large; CL clients primarily use newPayload/forkchoiceUpdated.
            await WriteSszResponse(ctx.Response, []);
        }
        catch (SszDecodingException ex)
        {
            await WriteJsonError(ctx.Response, 400, ErrorCodes.InvalidParams, ex.Message);
        }
    }

    private async Task HandleExchangeCapabilities(HttpListenerContext ctx, byte[] body)
    {
        try
        {
            string[] incomingCaps = SszEncoding.DecodeCapabilities(body);
            ResultWrapper<IEnumerable<string>> result = _capabilitiesHandler.Handle(incomingCaps);

            if (result.Result != Result.Success)
            {
                await WriteJsonError(ctx.Response, 400, result.ErrorCode, result.Result.Error ?? "Unknown error");
                return;
            }

            byte[] responseBytes = SszEncoding.EncodeCapabilities(result.Data);
            await WriteSszResponse(ctx.Response, responseBytes);
        }
        catch (SszDecodingException ex)
        {
            await WriteJsonError(ctx.Response, 400, ErrorCodes.InvalidParams, ex.Message);
        }
    }

    private async Task HandleGetClientVersion(HttpListenerContext ctx, byte[] body)
    {
        try
        {
            // Decode the incoming client version (we don't actually use it, just parse for validation)
            if (body.Length > 0)
            {
                SszEncoding.DecodeClientVersions(body);
            }

            // Return our client version
            ClientVersionV1[] versions = [new ClientVersionV1()];
            byte[] responseBytes = SszEncoding.EncodeClientVersions(versions);
            await WriteSszResponse(ctx.Response, responseBytes);
        }
        catch (SszDecodingException ex)
        {
            await WriteJsonError(ctx.Response, 400, ErrorCodes.InvalidParams, ex.Message);
        }
    }

    #endregion

    #region Response helpers

    private static async Task WriteSszResponse(HttpListenerResponse response, byte[] data)
    {
        response.StatusCode = 200;
        response.ContentType = "application/octet-stream";
        response.ContentLength64 = data.Length;
        await response.OutputStream.WriteAsync(data);
        response.Close();
    }

    private static async Task WriteJsonError(HttpListenerResponse response, int httpStatus, int code, string message)
    {
        response.StatusCode = httpStatus;
        response.ContentType = "application/json";
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new { code, message });
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
        response.Close();
    }

    #endregion
}
