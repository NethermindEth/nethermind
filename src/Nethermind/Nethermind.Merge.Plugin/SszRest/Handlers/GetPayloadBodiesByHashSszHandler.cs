// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core.Crypto;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v{N}/payloads/bodies/by-hash</c>, the SSZ-REST equivalent
/// of <c>engine_getPayloadBodiesByHashV{N}</c>.
/// </summary>
public sealed class GetPayloadBodiesByHashSszHandler<TResult>(
    int version,
    IHandler<IReadOnlyList<Hash256>, IEnumerable<TResult?>> handler,
    Func<IEnumerable<TResult?>, (byte[] buffer, int length)> encode) : SszEndpointHandlerBase
{
    public override string HttpMethod => "POST";
    public override string Resource => "payloads/bodies/by-hash";
    public override int? Version => version;

    public override async Task HandleAsync(HttpContext ctx, int v, string extra, byte[] body)
    {
        Hash256[] hashes = SszCodec.DecodeGetPayloadBodiesByHashRequest(body);
        await WriteSszResultAsync(ctx, handler.Handle(hashes), encode);
    }
}
