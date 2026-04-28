// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Consensus.Producers;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v{N}/forkchoice</c>, the SSZ-REST equivalent of
/// <c>engine_forkchoiceUpdatedV{N}</c>.
/// </summary>
public sealed class ForkchoiceUpdatedSszHandler(IForkchoiceUpdatedHandler handler) : SszEndpointHandlerBase
{
    private readonly IForkchoiceUpdatedHandler _handler = handler;

    public override string HttpMethod => "POST";
    public override string Resource => "forkchoice";

    public override async Task HandleAsync(HttpContext ctx, int version, string extra, ReadOnlyMemory<byte> body)
    {
        (ForkchoiceStateV1 state, PayloadAttributes? attrs) =
            SszCodec.DecodeForkchoiceUpdatedRequest(body.Span, version);

        await WriteSszResultAsync(
            ctx,
            await _handler.Handle(state, attrs, version),
            SszCodec.EncodeForkchoiceUpdatedResponse);
    }
}
