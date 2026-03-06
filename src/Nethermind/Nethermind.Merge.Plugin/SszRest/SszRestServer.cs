// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Consensus;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest;

/// <summary>
/// Response from SSZ-REST handler.
/// </summary>
public readonly record struct SszRestResponse(int StatusCode, string ContentType, byte[] Body);

/// <summary>
/// EIP-8161 SSZ-REST Engine API handler.
/// Serves binary SSZ-encoded payloads over REST endpoints on the same port as JSON-RPC.
/// Requests with paths starting with /engine/ are routed here by the ASP.NET Core middleware.
/// This class has no ASP.NET Core dependencies — it takes raw path/body and returns a response.
/// </summary>
public sealed class SszRestHandler
{
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

    public SszRestHandler(
        IAsyncHandler<ExecutionPayload, PayloadStatusV1> newPayloadHandler,
        IForkchoiceUpdatedHandler forkchoiceUpdatedHandler,
        IAsyncHandler<byte[], ExecutionPayload?> getPayloadV1Handler,
        IAsyncHandler<byte[], GetPayloadV2Result?> getPayloadV2Handler,
        IAsyncHandler<byte[], GetPayloadV3Result?> getPayloadV3Handler,
        IAsyncHandler<byte[], GetPayloadV4Result?> getPayloadV4Handler,
        IAsyncHandler<byte[], GetPayloadV5Result?> getPayloadV5Handler,
        IHandler<IEnumerable<string>, IEnumerable<string>> capabilitiesHandler,
        IAsyncHandler<byte[][], IEnumerable<BlobAndProofV1?>> getBlobsHandler,
        ILogManager logManager)
    {
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
    }

    /// <summary>
    /// Handles a request. Path should start with /engine/v{N}/{method}.
    /// </summary>
    public async Task<SszRestResponse> HandleAsync(string httpMethod, string path, byte[] body)
    {
        try
        {
            if (!string.Equals(httpMethod, "POST", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(httpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                return MakeJsonError(405, -32600, "Method not allowed");
            }

            return await RouteRequest(path, body);
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"SSZ-REST request processing error: {ex.Message}", ex);
            return MakeJsonError(500, -32603, "Internal error");
        }
    }

    private async Task<SszRestResponse> RouteRequest(string path, byte[] body)
    {
        if (!TryParsePath(path, out int version, out string method))
        {
            return MakeJsonError(404, -32601, $"Unknown endpoint: {path}");
        }

        if (_logger.IsInfo) _logger.Info($"SSZ-REST << engine_v{version}_{method} ({body.Length} bytes)");

        if (method.StartsWith("payloads/", StringComparison.Ordinal))
        {
            string payloadIdHex = method["payloads/".Length..];
            return await HandleGetPayloadByPath(payloadIdHex, version);
        }

        return method switch
        {
            "new_payload" or "payloads" => await HandleNewPayload(body, version),
            "forkchoice_updated" or "forkchoice" => await HandleForkchoiceUpdated(body, version),
            "get_payload" => await HandleGetPayload(SszRestCodec.DecodeGetPayloadRequest(body), version),
            "get_blobs" or "blobs" => await HandleGetBlobs(body),
            "exchange_capabilities" or "capabilities" => await HandleExchangeCapabilities(body),
            "get_client_version" or "client/version" => await HandleGetClientVersion(body),
            _ => MakeJsonError(404, -32601, $"Unknown method: {method}")
        };
    }

    private static bool TryParsePath(string path, out int version, out string method)
    {
        version = 0;
        method = string.Empty;

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

    private async Task<SszRestResponse> HandleNewPayload(byte[] body, int version)
    {
        try
        {
            (ExecutionPayloadV3 payload, byte[]?[] _, Hash256? parentBeaconBlockRoot, byte[][]? executionRequests) =
                SszRestCodec.DecodeNewPayloadRequest(body, version);

            payload.ParentBeaconBlockRoot = parentBeaconBlockRoot;
            payload.ExecutionRequests = executionRequests;

            ResultWrapper<PayloadStatusV1> result = await _newPayloadHandler.HandleAsync(payload);

            if (result.Result != Result.Success)
                return MakeJsonError(400, result.ErrorCode, result.Result.Error ?? "Unknown error");

            return MakeSszResponse(SszRestCodec.EncodePayloadStatus(result.Data));
        }
        catch (SszDecodingException ex)
        {
            return MakeJsonError(400, ErrorCodes.InvalidParams, ex.Message);
        }
    }

    private async Task<SszRestResponse> HandleForkchoiceUpdated(byte[] body, int version)
    {
        try
        {
            (ForkchoiceStateV1 state, PayloadAttributes? attributes) =
                SszRestCodec.DecodeForkchoiceUpdatedRequest(body, version);

            ResultWrapper<ForkchoiceUpdatedV1Result> result = await _forkchoiceUpdatedHandler.Handle(state, attributes, version);

            if (result.Result != Result.Success)
                return MakeJsonError(400, result.ErrorCode, result.Result.Error ?? "Unknown error");

            return MakeSszResponse(SszRestCodec.EncodeForkchoiceUpdatedResponse(result.Data));
        }
        catch (SszDecodingException ex)
        {
            return MakeJsonError(400, ErrorCodes.InvalidParams, ex.Message);
        }
    }

    private async Task<SszRestResponse> HandleGetPayloadByPath(string payloadIdHex, int version)
    {
        try
        {
            if (payloadIdHex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                payloadIdHex = payloadIdHex[2..];

            byte[] payloadId = Convert.FromHexString(payloadIdHex);
            if (payloadId.Length < 8)
            {
                byte[] padded = new byte[8];
                payloadId.CopyTo(padded, 0);
                payloadId = padded;
            }

            if (_logger.IsInfo) _logger.Info($"SSZ-REST << GET engine_v{version}_get_payload (id={payloadIdHex})");
            return await HandleGetPayload(payloadId, version);
        }
        catch (FormatException)
        {
            return MakeJsonError(400, ErrorCodes.InvalidParams, $"Invalid payload ID hex: {payloadIdHex}");
        }
    }

    private async Task<SszRestResponse> HandleGetPayload(byte[] payloadId, int version)
    {
        try
        {
            switch (version)
            {
                case 1:
                {
                    ResultWrapper<ExecutionPayload?> result = await _getPayloadV1Handler.HandleAsync(payloadId);
                    if (result.Result != Result.Success || result.Data is null)
                        return MakeJsonError(400, result.ErrorCode, result.Result.Error ?? "Payload not found");
                    return MakeSszResponse(SszRestCodec.EncodeExecutionPayload(result.Data, 1));
                }
                case 2:
                {
                    ResultWrapper<GetPayloadV2Result?> result = await _getPayloadV2Handler.HandleAsync(payloadId);
                    if (result.Result != Result.Success || result.Data is null)
                        return MakeJsonError(400, result.ErrorCode, result.Result.Error ?? "Payload not found");
                    return MakeSszResponse(SszRestCodec.EncodeGetPayloadResponse(
                        result.Data.ExecutionPayload, result.Data.BlockValue, null, false, null, version));
                }
                case 3:
                {
                    ResultWrapper<GetPayloadV3Result?> result = await _getPayloadV3Handler.HandleAsync(payloadId);
                    if (result.Result != Result.Success || result.Data is null)
                        return MakeJsonError(400, result.ErrorCode, result.Result.Error ?? "Payload not found");
                    return MakeSszResponse(SszRestCodec.EncodeGetPayloadResponse(
                        result.Data.ExecutionPayload, result.Data.BlockValue, result.Data.BlobsBundle, result.Data.ShouldOverrideBuilder, null, version));
                }
                case 4:
                {
                    ResultWrapper<GetPayloadV4Result?> result = await _getPayloadV4Handler.HandleAsync(payloadId);
                    if (result.Result != Result.Success || result.Data is null)
                        return MakeJsonError(400, result.ErrorCode, result.Result.Error ?? "Payload not found");
                    return MakeSszResponse(SszRestCodec.EncodeGetPayloadResponse(
                        result.Data.ExecutionPayload, result.Data.BlockValue, result.Data.BlobsBundle, result.Data.ShouldOverrideBuilder, result.Data.ExecutionRequests, version));
                }
                case 5:
                {
                    ResultWrapper<GetPayloadV5Result?> result = await _getPayloadV5Handler.HandleAsync(payloadId);
                    if (result.Result != Result.Success || result.Data is null)
                        return MakeJsonError(400, result.ErrorCode, result.Result.Error ?? "Payload not found");
                    return MakeSszResponse(SszRestCodec.EncodeGetPayloadResponse(
                        result.Data.ExecutionPayload, result.Data.BlockValue, null, result.Data.ShouldOverrideBuilder, result.Data.ExecutionRequests, version));
                }
                default:
                    return MakeJsonError(400, ErrorCodes.InvalidParams, $"Unsupported getPayload version: {version}");
            }
        }
        catch (SszDecodingException ex)
        {
            return MakeJsonError(400, ErrorCodes.InvalidParams, ex.Message);
        }
    }

    private async Task<SszRestResponse> HandleGetBlobs(byte[] body)
    {
        try
        {
            byte[][] hashes = SszRestCodec.DecodeGetBlobsRequest(body);
            ResultWrapper<IEnumerable<BlobAndProofV1?>> result = await _getBlobsHandler.HandleAsync(hashes);

            if (result.Result != Result.Success)
                return MakeJsonError(400, result.ErrorCode, result.Result.Error ?? "Unknown error");

            return MakeSszResponse([]);
        }
        catch (SszDecodingException ex)
        {
            return MakeJsonError(400, ErrorCodes.InvalidParams, ex.Message);
        }
    }

    private Task<SszRestResponse> HandleExchangeCapabilities(byte[] body)
    {
        try
        {
            string[] incomingCaps = SszRestCodec.DecodeCapabilities(body);
            ResultWrapper<IEnumerable<string>> result = _capabilitiesHandler.Handle(incomingCaps);

            if (result.Result != Result.Success)
                return Task.FromResult(MakeJsonError(400, result.ErrorCode, result.Result.Error ?? "Unknown error"));

            return Task.FromResult(MakeSszResponse(SszRestCodec.EncodeCapabilities(result.Data)));
        }
        catch (SszDecodingException ex)
        {
            return Task.FromResult(MakeJsonError(400, ErrorCodes.InvalidParams, ex.Message));
        }
    }

    private Task<SszRestResponse> HandleGetClientVersion(byte[] body)
    {
        try
        {
            if (body.Length > 0)
                SszRestCodec.DecodeClientVersions(body);

            ClientVersionV1[] versions = [new ClientVersionV1()];
            return Task.FromResult(MakeSszResponse(SszRestCodec.EncodeClientVersions(versions)));
        }
        catch (SszDecodingException ex)
        {
            return Task.FromResult(MakeJsonError(400, ErrorCodes.InvalidParams, ex.Message));
        }
    }

    #endregion

    #region Response helpers

    private static SszRestResponse MakeSszResponse(byte[] data)
        => new(200, "application/octet-stream", data);

    private static SszRestResponse MakeJsonError(int httpStatus, int code, string message)
        => new(httpStatus, "application/json", JsonSerializer.SerializeToUtf8Bytes(new { code, message }));

    #endregion
}
