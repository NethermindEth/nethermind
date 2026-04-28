// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v{N}/payloads/bodies/by-range</c>, the SSZ-REST equivalent
/// of <c>engine_getPayloadBodiesByRangeV{N}</c>.
/// </summary>
public sealed class GetPayloadBodiesByRangeSszHandler<TResult>(
    int version,
    Func<long, long, Task<ResultWrapper<IEnumerable<TResult?>>>> handler,
    Func<IEnumerable<TResult?>, (byte[] buffer, int length)> encode) : SszEndpointHandlerBase
{
    public override string HttpMethod => "POST";
    public override string Resource => "payloads/bodies/by-range";
    public override int? Version => version;

    public override async Task HandleAsync(HttpContext ctx, int v, string extra, byte[] body)
    {
        (long start, long count) = SszCodec.DecodeGetPayloadBodiesByRangeRequest(body);
        await WriteSszResultAsync(ctx, await handler(start, count), encode);
    }
}
