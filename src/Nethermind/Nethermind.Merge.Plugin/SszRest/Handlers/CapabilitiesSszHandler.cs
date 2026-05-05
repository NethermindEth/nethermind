// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Merge.Plugin.Handlers;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>POST /engine/v{N}/capabilities</c>, the SSZ-REST equivalent of
/// <c>engine_exchangeCapabilities</c>.
/// </summary>
public sealed class CapabilitiesSszHandler(IHandler<IEnumerable<string>, IEnumerable<string>> handler) : SszEndpointHandlerBase
{
    private readonly IHandler<IEnumerable<string>, IEnumerable<string>> _handler = handler;

    public override string HttpMethod => "POST";
    public override string Resource => "capabilities";
    public override int? Version => 1;

    public override async Task HandleAsync(HttpContext ctx, int version, string extra, ReadOnlyMemory<byte> body)
    {
        string[] caps = SszCodec.DecodeCapabilitiesRequest(body.Span);
        await WriteSszResultAsync(ctx, _handler.Handle(caps),
            result => SszCodec.EncodeCapabilitiesResponse(AsReadOnlyList(result)!));
    }
}
