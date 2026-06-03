// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.JsonRpc;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>GET /engine/v2/{fork}/bodies?from=N&amp;count=M</c>, the SSZ-REST equivalent
/// of <c>engine_getPayloadBodiesByRangeV{N}</c>. Generic over a per-version descriptor
/// so adding a Vn+1 endpoint is one new descriptor + one DI line.
/// </summary>
public sealed class GetPayloadBodiesByRangeSszHandler<TVersion, TResult>(IEngineRpcModule engineModule)
    : SszEndpointHandlerBase
    where TVersion : struct, IPayloadBodiesByRangeVersion<TResult>
    where TResult : class
{
    // per spec: MAX_BODIES_REQUEST = 2**5 = 32. The previous value of 128 matched MAX_BLOBS_REQUEST but contradicted the bodies spec.
    private const int MaxPayloadBodiesRequest = 32;

    public override string HttpMethod => "GET";
    public override string Resource => SszRestPaths.PayloadBodiesByRange;
    public override int? Version => TVersion.VersionNumber;

    public override async Task HandleAsync(HttpContext ctx, int v, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        // body is empty for GET; parameters come from the query string.
        if (!long.TryParse(ctx.Request.Query["from"], out long start) || start <= 0)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest,
                "Missing or invalid 'from' query parameter: must be a positive integer block number",
                SszRestErrorCodes.InvalidRequest);
            return;
        }
        if (!long.TryParse(ctx.Request.Query["count"], out long count) || count <= 0)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest,
                "Missing or invalid 'count' query parameter: must be a positive integer",
                SszRestErrorCodes.InvalidRequest);
            return;
        }
        if (count > MaxPayloadBodiesRequest)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest,
                $"count {count} exceeds the limit of {MaxPayloadBodiesRequest}",
                SszRestErrorCodes.InvalidRequest);
            return;
        }
        ResultWrapper<IReadOnlyList<TResult?>> result = await TVersion.Call(engineModule, start, count);
        await WriteSszResultAsync(ctx, result, TVersion.Encode);
    }
}
