// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>GET /engine/v2/capabilities</c>, the HTTP/REST equivalent of
/// <c>engine_exchangeCapabilities</c>.
/// </summary>
public sealed class CapabilitiesSszHandler : SszEndpointHandlerBase
{
    public override string HttpMethod => "GET";
    public override string Resource => SszRestPaths.Capabilities;
    public override int? Version => null;

    private static readonly string _supportedForksJson =
        JsonSerializer.Serialize(SszRestPaths.SupportedForksOrdered);

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        await ctx.Response.WriteAsync($$"""
            {
              "supported_forks": {{_supportedForksJson}},
              "fork_scoped_endpoints": ["payloads", "forkchoice", "bodies"],
              "independently_versioned": {
                "blobs": ["v1", "v2", "v3", "v4"]
              },
              "unscoped_endpoints": ["capabilities", "identity"],
              "limits": {
                "bodies.max_count": 32,
                "blobs.max_versioned_hashes": 128,
                "payload.max_bytes": {{SszMiddleware.MaxBodySize}}
              }
            }
            """, ctx.RequestAborted);
    }
}
