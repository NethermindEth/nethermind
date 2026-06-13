// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>GET /engine/v2/identity</c>, the HTTP/REST equivalent of
/// <c>engine_getClientVersionV1</c>.
/// </summary>
public sealed class ClientVersionSszHandler(IEngineRpcModule engineModule, ILogManager logManager) : SszEndpointHandlerBase
{
    private readonly IEngineRpcModule _engineModule = engineModule;
    private readonly ILogger _logger = logManager.GetClassLogger<ClientVersionSszHandler>();

    private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions =
        new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    private static readonly System.Text.Json.JsonSerializerOptions _headerJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    public override string HttpMethod => "GET";
    public override string Resource => SszRestPaths.ClientVersion;
    public override int? Version => null;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        ClientVersionV1 clientVersion = TryParseClientVersionHeader(ctx);
        ResultWrapper<ClientVersionV1[]> result = _engineModule.engine_getClientVersionV1(clientVersion);

        if (result.Result.ResultType != ResultType.Success)
        {
            await WriteErrorAsync(ctx, ErrorCodeToHttpStatus(result.ErrorCode),
                result.Result.Error ?? "engine_getClientVersionV1 failed", result.ErrorCode);
            return;
        }

        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        string json = System.Text.Json.JsonSerializer.Serialize(result.Data, _jsonOptions);
        await ctx.Response.WriteAsync(json, ctx.RequestAborted);
    }

    private ClientVersionV1 TryParseClientVersionHeader(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("X-Engine-Client-Version", out StringValues headerValues) || headerValues.Count == 0)
            return default;

        string? headerVal = headerValues[0];
        if (string.IsNullOrWhiteSpace(headerVal))
            return default;

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<ClientVersionV1>(headerVal, _headerJsonOptions);
        }
        catch (Exception ex)
        {
            if (_logger.IsTrace) _logger.Trace($"SSZ-REST: ignoring malformed X-Engine-Client-Version header: {ex.Message}");
            return default;
        }
    }
}
