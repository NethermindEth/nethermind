// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v{N}/forkchoice</c>, the SSZ-REST equivalent of
/// <c>engine_forkchoiceUpdatedV{N}</c>. Generic over a per-version descriptor
/// so adding V5 is one new descriptor struct + one DI line — no version switch.
/// </summary>
public sealed class ForkchoiceUpdatedSszHandler<TVersion, TWire>(IEngineRpcModule engineModule) : SszEndpointHandlerBase
    where TVersion : struct, IForkchoiceUpdatedVersion<TWire>
    where TWire : struct, ISszCodec<TWire>
{
    public override string HttpMethod => "POST";
    public override string Resource => "forkchoice";
    public override int? Version => TVersion.VersionNumber;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlyMemory<byte> body)
    {
        TWire.Decode(body.Span, out TWire wire);
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await TVersion.Call(engineModule, wire);
        await WriteSszResultAsync(ctx, result, SszCodec.EncodeForkchoiceUpdatedResponse);
    }
}
