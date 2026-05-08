// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;
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
    public override string HttpMethod => "POST";
    public override string Resource => "payloads/bodies/by-range";
    public override int? Version => TVersion.VersionNumber;

    public override async Task HandleAsync(HttpContext ctx, int v, ReadOnlyMemory<char> extra, ReadOnlyMemory<byte> body)
    {
        (long start, long count) = SszCodec.DecodeGetPayloadBodiesByRangeRequest(body.Span);
        ResultWrapper<IReadOnlyList<TResult?>> result = await TVersion.Call(engineModule, start, count);
        await WriteSszResultAsync(ctx, result, TVersion.Encode);
    }
}
