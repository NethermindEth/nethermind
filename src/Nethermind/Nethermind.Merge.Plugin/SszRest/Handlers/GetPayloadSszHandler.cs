// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>GET /engine/v{N}/payloads/{payloadId}</c>, the SSZ-REST equivalent of
/// <c>engine_getPayloadV{N}</c>.
/// </summary>
public sealed class GetPayloadSszHandler<TResult>(
    int version,
    Func<byte[], Task<ResultWrapper<TResult?>>> engineCall,
    Func<TResult, (byte[] buffer, int length)> encode) : SszEndpointHandlerBase
    where TResult : class
{
    public override string HttpMethod => "GET";
    public override string Resource => "payloads";
    public override int? Version => version;
    public override bool AcceptsPathExtra => true;

    public override async Task HandleAsync(HttpContext ctx, int v, string extra, ReadOnlyMemory<byte> body)
    {
        if (!TryParsePayloadId(extra, out byte[] id, out string err))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, err);
            return;
        }

        ResultWrapper<TResult?> result = await engineCall(id);

        ctx.Response.Headers["Cache-Control"] = "no-store";

        if (result.Data is null)
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        else
            await WriteSszPooledAsync(ctx, encode(result.Data));
    }
}
