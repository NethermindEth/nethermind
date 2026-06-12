// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core.Specs;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>GET /engine/v2/capabilities</c>, the HTTP/REST equivalent of
/// <c>engine_exchangeCapabilities</c>.
/// </summary>
/// <remarks>
/// The response body is fully determined by <see cref="ISpecProvider"/> state, which is
/// fixed for the lifetime of the EL. The body is built once on first request and reused.
/// </remarks>
public sealed class CapabilitiesSszHandler(ISpecProvider specProvider) : SszEndpointHandlerBase
{
    private byte[]? _cachedBody;

    public override string HttpMethod => "GET";
    public override string Resource => SszRestPaths.Capabilities;
    public override int? Version => null;

    public override Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        byte[] cached = _cachedBody ?? InitializeCachedBody();
        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentLength = cached.Length;
        return ctx.Response.Body.WriteAsync(cached, 0, cached.Length, ctx.RequestAborted);
    }

    private byte[] InitializeCachedBody()
    {
        // Benign race: two threads may both build the body on first hit; whoever wins the
        // CompareExchange wins the cache slot. Subsequent requests are lock-free.
        byte[] built = BuildBody(specProvider);
        return Interlocked.CompareExchange(ref _cachedBody, built, null) ?? built;
    }

    private static byte[] BuildBody(ISpecProvider specProvider)
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

        return Encoding.UTF8.GetBytes(
            $"{{\"supported_forks\":{supportedForksJson}," +
            $"\"fork_scoped_endpoints\":[\"payloads\",\"forkchoice\",\"bodies\"]," +
            $"\"independently_versioned\":{{\"blobs\":[\"v1\",\"v2\",\"v3\",\"v4\"]}}," +
            $"\"unscoped_endpoints\":[\"capabilities\",\"identity\"]," +
            $"\"limits\":{{" +
            $"\"bodies.max_count\":{SszRestLimits.MaxBodiesRequest}," +
            $"\"blobs.max_versioned_hashes\":{SszRestLimits.MaxBlobsRequest}," +
            $"\"payload.max_bytes\":{SszMiddleware.MaxBodySize}}}}}");
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
