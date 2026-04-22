// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>GET /engine/v5/payloads/{payloadId}</c>, the SSZ-REST equivalent of
/// <c>engine_getPayloadV5</c>.
/// </summary>
public sealed class GetPayloadV5SszHandler(IAsyncHandler<byte[], GetPayloadV5Result?> handler) : SszEndpointHandlerBase
{
    private readonly IAsyncHandler<byte[], GetPayloadV5Result?> _handler = handler;

    public override string HttpMethod => "GET";
    public override string Resource => "payloads";
    public override int? Version => 5;

    public override async Task HandleAsync(HttpContext ctx, int version, string extra, byte[] body)
    {
        if (extra.Length == 0)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "Missing payload ID");
            return;
        }

        string hex = extra.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? extra[2..] : extra;
        ResultWrapper<GetPayloadV5Result?> result = await _handler.HandleAsync(Convert.FromHexString(hex.AsSpan()));

        ctx.Response.Headers["Cache-Control"] = "no-store";

        if (result.Data is null)
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        else
            await WriteSszPooledAsync(ctx, SszCodec.EncodeGetPayloadV5Response(result.Data));
    }
}
