// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v{N}/capabilities</c>, the SSZ-REST equivalent of
/// <c>engine_exchangeCapabilities</c>.
/// </summary>
public sealed class CapabilitiesSszHandler(IEngineRpcModule engineModule) : SszEndpointHandlerBase
{
    public override string HttpMethod => "POST";
    public override string Resource => SszRestPaths.Capabilities;
    public override int? Version => 1;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlyMemory<byte> body)
    {
        string[] caps = SszCodec.DecodeCapabilitiesRequest(body.Span);
        await WriteSszResultAsync(ctx, engineModule.engine_exchangeCapabilities(caps),
            SszCodec.EncodeCapabilitiesResponse);
    }
}
