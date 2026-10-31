// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core;

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
        string supportedForksJson = JsonSerializer.Serialize(SszRestPaths.SupportedForksOrdered, SszRestJsonContext.Default.IEnumerableString);
        string forkScopedEndpointsJson = JsonSerializer.Serialize(ForkScopedEndpointNames(), SszRestJsonContext.Default.IEnumerableString);
        string blobVersionsJson = JsonSerializer.Serialize(BlobVersionNames(), SszRestJsonContext.Default.IEnumerableString);

        return Encoding.UTF8.GetBytes(
            $"{{\"supported_forks\":{supportedForksJson}," +
            $"\"fork_scoped_endpoints\":{forkScopedEndpointsJson}," +
            $"\"independently_versioned\":{{\"{SszRestPaths.Blobs}\":{blobVersionsJson}}}," +
            $"\"unscoped_endpoints\":[\"{SszRestPaths.Capabilities}\",\"{SszRestPaths.ClientVersion}\"]," +
            $"\"limits\":{{" +
            $"\"bodies.max_count\":{SszRestLimits.MaxBodiesRequest}," +
            $"\"blobs.max_versioned_hashes\":{SszRestLimits.MaxBlobsRequest}," +
            $"\"payload.max_bytes\":{SszMiddleware.MaxBodySize}}}}}");
    }

    // The spec advertises POST bodies/hash and GET bodies together as one "bodies" family.
    private static IEnumerable<string> ForkScopedEndpointNames() =>
        SszRestPaths.ForkScopedEndpoints
            .Select(static endpoint => endpoint.Resource is SszRestPaths.PayloadBodiesByHash ? SszRestPaths.PayloadBodiesByRange : endpoint.Resource)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> BlobVersionNames() =>
        Enumerable.Range(EngineApiVersions.GetBlobs.V1, EngineApiVersions.GetBlobs.Latest - EngineApiVersions.GetBlobs.V1 + 1).Select(static version => $"v{version}");
}
