// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v1/payloads/bodies/by-hash</c>, the SSZ-REST equivalent of
/// <c>engine_getPayloadBodiesByHashV1</c>.
/// </summary>
public sealed class GetPayloadBodiesByHashV1SszHandler(
    IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV1Result?>> handler) : SszEndpointHandlerBase
{
    private readonly IHandler<IReadOnlyList<Hash256>, IEnumerable<ExecutionPayloadBodyV1Result?>> _handler = handler;

    public override string HttpMethod => "POST";
    public override string Resource => "payloads/bodies/by-hash";
    public override int? Version => 1;

    public override async Task HandleAsync(HttpContext ctx, int version, string extra, byte[] body)
    {
        Hash256[] hashes = SszCodec.DecodeGetPayloadBodiesByHashRequest(body);
        await WriteSszResultAsync(ctx, _handler.Handle(hashes), SszCodec.EncodePayloadBodiesV1Response);
    }
}
