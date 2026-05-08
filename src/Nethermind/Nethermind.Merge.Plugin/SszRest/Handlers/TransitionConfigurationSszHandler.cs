// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v{N}/transition-configuration</c>, the SSZ-REST equivalent of
/// <c>engine_exchangeTransitionConfigurationV1</c>.
/// </summary>
public sealed class TransitionConfigurationSszHandler(IEngineRpcModule engineModule) : SszEndpointHandlerBase
{
    public override string HttpMethod => "POST";
    public override string Resource => SszRestPaths.TransitionConfiguration;
    public override int? Version => 1;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        TransitionConfigurationV1 tc = SszCodec.DecodeTransitionConfigurationRequest(body);
        await WriteSszResultAsync(ctx, engineModule.engine_exchangeTransitionConfigurationV1(tc),
            SszCodec.EncodeTransitionConfigurationResponse);
    }
}
