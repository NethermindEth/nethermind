// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core.Specs;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>GET /engine/v2/capabilities</c>, the HTTP/REST equivalent of
/// <c>engine_exchangeCapabilities</c>.
/// </summary>
public sealed class CapabilitiesSszHandler(ISpecProvider specProvider) : SszEndpointHandlerBase
{
    public override string HttpMethod => "GET";
    public override string Resource => SszRestPaths.Capabilities;
    public override int? Version => null;

    public override async Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        int timestampForkCount = ComputeTimestampForkCount(specProvider);

        string supportedForksJson;
        if (timestampForkCount == 0)
        {
            supportedForksJson = JsonSerializer.Serialize(SszRestPaths.SupportedForksOrdered);
        }
        else
        {
            int limit = Math.Min(timestampForkCount + 1, SszRestPaths.SupportedForksOrdered.Count);
            List<string> forkSlice = new(limit);
            for (int i = 0; i < limit; i++)
            {
                forkSlice.Add(SszRestPaths.SupportedForksOrdered[i]);
            }
            supportedForksJson = JsonSerializer.Serialize(forkSlice);
        }

        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        await ctx.Response.WriteAsync($$"""
            {
              "supported_forks": {{supportedForksJson}},
              "fork_scoped_endpoints": ["payloads", "forkchoice", "bodies"],
              "independently_versioned": {
                "blobs": ["v1", "v2", "v3", "v4"]
              },
              "unscoped_endpoints": ["capabilities", "identity"],
              "limits": {
                "bodies.max_count": {{SszRestLimits.MaxBodiesRequest}},
                "blobs.max_versioned_hashes": {{SszRestLimits.MaxBlobsRequest}},
                "payload.max_bytes": {{SszMiddleware.MaxBodySize}}
              }
            }
            """, ctx.RequestAborted);
    }

    private static int ComputeTimestampForkCount(ISpecProvider specProvider)
    {
        int count = 0;
        IReleaseSpec? lastSeen = null;

        foreach (ForkActivation fa in specProvider.TransitionActivations)
        {
            if (fa.Timestamp is null)
                continue;

            IReleaseSpec s = specProvider.GetSpec(fa);
            if (ReferenceEquals(s, lastSeen))
                continue;

            count++;
            lastSeen = s;

            if (count >= SszRestPaths.SupportedForksOrdered.Count - 1)
                break;
        }

        return count;
    }
}
