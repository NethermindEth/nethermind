// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Nethermind.Merge.Plugin.SszRest.Handlers;

/// <summary>
/// Handles <c>GET /engine/v1/capabilities</c>, the HTTP/REST equivalent of
/// <c>engine_exchangeCapabilities</c>.
/// </summary>
/// <remarks>
/// <c>supported_forks</c> lists every SSZ schema implemented by this client, independent of the network's fork
/// schedule. Callers select a schema on fork-scoped requests with <c>Eth-Execution-Version</c>.
/// </remarks>
public sealed class CapabilitiesSszHandler : SszEndpointHandlerBase
{
    private static readonly byte[] _responseBody = BuildBody();

    public override string HttpMethod => "GET";
    public override string Resource => SszRestPaths.Capabilities;
    public override int? Version => null;

    public override Task HandleAsync(HttpContext ctx, int version, ReadOnlyMemory<char> extra, ReadOnlySequence<byte> body)
    {
        ctx.Response.ContentType = "application/json";
        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentLength = _responseBody.Length;
        return ctx.Response.Body.WriteAsync(_responseBody, 0, _responseBody.Length, ctx.RequestAborted);
    }

    private static byte[] BuildBody()
    {
        string supportedForksJson = JsonSerializer.Serialize(SszRestPaths.SupportedForksOrdered, SszRestJsonContext.Default.IReadOnlyListString);

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
}
