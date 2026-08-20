// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>SSZ-REST equivalent of <c>engine_getInclusionListV1</c>.</summary>
public sealed class GetInclusionListSszHandler(IEngineRpcModule engineModule) : SszEndpointHandlerBase
{
    public override string HttpMethod => "GET";
    public override string Resource => SszRestPaths.InclusionList;
    public override int? Version => EngineApiVersions.GetInclusionList.V1;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        ctx.Response.Headers.CacheControl = "no-store";
        // WriteSszResultAsync disposes the ResultWrapper (and its IDisposable InclusionListBytes) after encoding.
        ResultWrapper<InclusionListBytes> result = await engineModule.engine_getInclusionListV1();
        await WriteSszResultAsync(ctx, result, SszCodec.EncodeInclusionListResponse);
    }
}
