// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Data;
using System;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v{N}/transition-configuration</c>, the SSZ-REST equivalent of
/// <c>engine_exchangeTransitionConfigurationV1</c>.
/// </summary>
public sealed class TransitionConfigurationSszHandler(
    IHandler<TransitionConfigurationV1, TransitionConfigurationV1> handler) : SszEndpointHandlerBase
{
    private readonly IHandler<TransitionConfigurationV1, TransitionConfigurationV1> _handler = handler;

    public override string HttpMethod => "POST";
    public override string Resource => "transition-configuration";

    public override async Task HandleAsync(HttpContext ctx, int version, string extra, ReadOnlyMemory<byte> body)
    {
        TransitionConfigurationV1 tc = SszCodec.DecodeTransitionConfigurationRequest(body.Span);
        await WriteSszResultAsync(ctx, _handler.Handle(tc), SszCodec.EncodeTransitionConfigurationResponse);
    }
}
