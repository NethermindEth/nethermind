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
/// Handles <c>POST /engine/v{N}/payloads/bodies/by-range</c>, the SSZ-REST equivalent
/// of <c>engine_getPayloadBodiesByRangeV{N}</c>. Generic over a per-version descriptor
/// so adding a Vn+1 endpoint is one new descriptor + one DI line.
/// </summary>
public sealed class GetPayloadBodiesByRangeSszHandler<TVersion, TResult>(IEngineRpcModule engineModule)
    : SszEndpointHandlerBase
    where TVersion : struct, IPayloadBodiesByRangeVersion<TResult>
    where TResult : class
{
    private const int MaxPayloadBodiesRequest = 32;

    public override string HttpMethod => "POST";
    public override string Resource => SszRestPaths.PayloadBodiesByRange;
    public override int? Version => TVersion.VersionNumber;

    public override async Task HandleAsync(HttpContext ctx, int v, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        (long start, long count) = SszCodec.DecodeGetPayloadBodiesByRangeRequest(body);
        if (count > MaxPayloadBodiesRequest)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest,
                $"count {count} exceeds the SSZ limit of {MaxPayloadBodiesRequest}");
            return;
        }
        ResultWrapper<IReadOnlyList<TResult?>> result = await TVersion.Call(engineModule, start, count);
        await WriteSszResultAsync(ctx, result, TVersion.Encode);
    }
}
