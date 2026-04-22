// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v2/payloads/bodies/by-range</c>, the SSZ-REST equivalent of
/// <c>engine_getPayloadBodiesByRangeV2</c>.
/// </summary>
public sealed class GetPayloadBodiesByRangeV2SszHandler(
    IGetPayloadBodiesByRangeV2Handler handler) : SszEndpointHandlerBase
{
    private readonly IGetPayloadBodiesByRangeV2Handler _handler = handler;

    public override string HttpMethod => "POST";
    public override string Resource => "payloads/bodies/by-range";
    public override int? Version => 2;

    public override async Task HandleAsync(HttpContext ctx, int version, string extra, byte[] body)
    {
        (long start, long count) = SszCodec.DecodeGetPayloadBodiesByRangeRequest(body);
        await WriteSszResultAsync(ctx, await _handler.Handle(start, count), SszCodec.EncodePayloadBodiesV2Response);
    }
}
