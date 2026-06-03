// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>GET /engine/v2/identity</c>, the HTTP/REST equivalent of
/// <c>engine_getClientVersionV1</c>.
/// </summary>
public sealed class ClientVersionSszHandler(IEngineRpcModule engineModule) : SszEndpointHandlerBase
{
    private readonly IEngineRpcModule _engineModule = engineModule;

    public override string HttpMethod => "GET";
    public override string Resource => SszRestPaths.ClientVersion;
    public override int? Version => null;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        ClientVersionV1 clientVersion = ctx.Items.TryGetValue("X-Engine-Client-Version", out object? clvObj) && clvObj is ClientVersionV1 clv
            ? clv
            : default;
        ResultWrapper<ClientVersionV1[]> result = _engineModule.engine_getClientVersionV1(clientVersion);

        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        string json = System.Text.Json.JsonSerializer.Serialize(result.Data, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });
        await ctx.Response.WriteAsync(json, ctx.RequestAborted);
    }
}
