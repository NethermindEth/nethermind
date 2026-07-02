// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Config;
using Nethermind.Core.Authentication;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.SszRest.Handlers;

namespace Nethermind.Merge.Plugin.SszRest;

/// <summary>
/// ASP.NET Core middleware that routes binary SSZ-REST engine API calls to
/// the appropriate <see cref="ISszEndpointHandler"/>.
/// </summary>
public sealed class SszMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IJsonRpcUrlCollection _urlCollection;
    private readonly IRpcAuthentication _auth;
    private readonly ILogger _logger;
    private readonly CancellationToken _processExitToken;

    private const string EnginePrefix = "/engine/v2/";

    /// <summary>Maximum allowed request body size in bytes (64 MiB), mirroring the spec's <c>payload.max_bytes</c> (execution-apis#793).</summary>
    public const int MaxBodySize = 0x4000000;

    private readonly FrozenDictionary<string, List<ISszEndpointHandler>> _postRoutes;
    private readonly FrozenDictionary<string, List<ISszEndpointHandler>> _getRoutes;
    private readonly FrozenDictionary<string, List<ISszEndpointHandler>>.AlternateLookup<ReadOnlySpan<char>> _postLookup;
    private readonly FrozenDictionary<string, List<ISszEndpointHandler>>.AlternateLookup<ReadOnlySpan<char>> _getLookup;

    // Verb-agnostic set of resources whose registered name spans several path segments ("payloads/witness",
    // "bodies/hash"). Used to decide whether a request path is a whole resource or a resource + path extra.
    private readonly FrozenSet<string> _multiSegmentResources;
    private readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> _multiSegmentLookup;

    private enum SszRequestKind { NotEngine, EngineWrongMediaType, EngineOk }

    public SszMiddleware(
        RequestDelegate next,
        IJsonRpcUrlCollection urlCollection,
        IRpcAuthentication auth,
        IEnumerable<ISszEndpointHandler> handlers,
        IProcessExitSource processExitSource,
        ILogManager logManager)
    {
        _next = next;
        _urlCollection = urlCollection;
        _auth = auth;
        _logger = logManager.GetClassLogger<SszMiddleware>();
        _processExitToken = processExitSource.Token;
        (_postRoutes, _getRoutes) = BuildRoutes(handlers);
        _postLookup = _postRoutes.GetAlternateLookup<ReadOnlySpan<char>>();
        _getLookup = _getRoutes.GetAlternateLookup<ReadOnlySpan<char>>();

        HashSet<string> multiSegment = new(StringComparer.OrdinalIgnoreCase);
        foreach (string resource in _postRoutes.Keys) if (resource.Contains('/')) multiSegment.Add(resource);
        foreach (string resource in _getRoutes.Keys) if (resource.Contains('/')) multiSegment.Add(resource);
        _multiSegmentResources = multiSegment.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        _multiSegmentLookup = _multiSegmentResources.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    private static (FrozenDictionary<string, List<ISszEndpointHandler>> post,
                    FrozenDictionary<string, List<ISszEndpointHandler>> get)
        BuildRoutes(IEnumerable<ISszEndpointHandler> handlers)
    {
        Dictionary<string, List<ISszEndpointHandler>> postDict = [];
        Dictionary<string, List<ISszEndpointHandler>> getDict = [];

        foreach (ISszEndpointHandler h in handlers)
        {
            Dictionary<string, List<ISszEndpointHandler>> dict =
                h.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase)
                    ? getDict
                    : postDict;

            if (!dict.TryGetValue(h.Resource, out List<ISszEndpointHandler>? list))
                dict[h.Resource] = list = [];

            list.Add(h);
        }

        FrozenDictionary<string, List<ISszEndpointHandler>> post = postDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        FrozenDictionary<string, List<ISszEndpointHandler>> get = getDict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        return (post, get);
    }

    public Task InvokeAsync(HttpContext ctx)
    {
        SszRequestKind kind = ClassifySszRequest(ctx);

        if (kind == SszRequestKind.NotEngine)
            return _next(ctx);

        if (_processExitToken.IsCancellationRequested)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        }

        if (!_urlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl? url) || !url.IsAuthenticated || !url.RpcEndpoint.HasFlag(RpcEndpoint.Http))
        {
            return _next(ctx);
        }

        if (kind == SszRequestKind.EngineWrongMediaType)
        {
            Metrics.SszRestRequestsClientErrorTotal++;
            return SszEndpointHandlerBase.WriteErrorAsync(
                ctx,
                StatusCodes.Status415UnsupportedMediaType,
                "Engine API hot-path endpoints require Content-Type: application/octet-stream (POST) " +
                "or Accept: application/octet-stream (GET)",
                SszRestErrorCodes.UnsupportedMediaType);
        }

        Metrics.SszRestRequestsTotal++;
        return ProcessSszRequestAsync(ctx);
    }

    private async Task ProcessSszRequestAsync(HttpContext ctx)
    {
        string? authHeader = ctx.Request.Headers.Authorization;
        if (authHeader is null || !await _auth.Authenticate(authHeader))
        {
            Metrics.SszRestRequestsClientErrorTotal++;
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status401Unauthorized,
                "Authentication error");
        }
        else if (!TryRoute(ctx.Request.Path.Value ?? string.Empty, out int version, out string? fork,
                     out ReadOnlyMemory<char> pathSegment, out bool unsupportedFork))
        {
            Metrics.SszRestRequestsClientErrorTotal++;
            if (unsupportedFork)
            {
                await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status400BadRequest,
                    $"Fork '{fork}' is not supported by this EL",
                    MergeErrorCodes.UnsupportedFork);
            }
            else
            {
                await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status404NotFound,
                    "Unknown SSZ endpoint", SszRestErrorCodes.MethodNotFound);
            }
        }
        else if (!TryResolveHandler(ctx.Request.Method, pathSegment, version, fork, out ISszEndpointHandler? handler, out ReadOnlyMemory<char> extra))
        {
            Metrics.SszRestRequestsClientErrorTotal++;
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status404NotFound,
                $"Unknown method: {ctx.Request.Method} /engine/v2/{pathSegment.Span}",
                SszRestErrorCodes.MethodNotFound);
        }
        else
        {
            if (fork is not null)
            {
                ctx.Items["SszRouteFork"] = fork;
            }

            if (_logger.IsTrace)
            {
                _logger.Trace(extra.IsEmpty
                    ? $"SSZ-REST {ctx.Request.Method} /engine/v2/{pathSegment.Span}"
                    : $"SSZ-REST {ctx.Request.Method} /engine/v2/{pathSegment.Span}/{extra.Span}");
            }

            await DispatchAsync(ctx, handler!, version, extra);
        }
    }

    /// <summary>Shared body-read + handler invocation + metrics + error mapping for the fork-routed endpoints.</summary>
    private async Task DispatchAsync(HttpContext ctx, ISszEndpointHandler handler, int version, ReadOnlyMemory<char> extra)
    {
        PipeReader reader = ctx.Request.BodyReader;
        ReadOnlySequence<byte> body = default;
        bool bodyRead = false;
        try
        {
            body = await ReadBodyAsync(ctx, reader);
            bodyRead = true;
            Metrics.SszRestRequestBytesTotal += body.Length;

            await handler.HandleAsync(ctx, version, extra, body);

            int status = ctx.Response.StatusCode;
            switch (status)
            {
                case >= 200 and < 300:
                    Metrics.SszRestRequestsSuccessTotal++;
                    break;
                case >= 400 and < 500:
                    Metrics.SszRestRequestsClientErrorTotal++;
                    break;
                case >= 500:
                    Metrics.SszRestRequestsServerErrorTotal++;
                    break;
            }
        }
        catch (InvalidOperationException ex) when (!bodyRead)
        {
            Metrics.SszRestRequestsClientErrorTotal++;
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status413PayloadTooLarge, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            // Malformed SSZ encoding is 400 with type=ssz-decode-error, no detail (execution-apis#793).
            Metrics.SszRestDecodeFailuresTotal++;
            Metrics.SszRestRequestsClientErrorTotal++;
            if (_logger.IsDebug) _logger.Debug($"SSZ-REST malformed body at {ctx.Request.Path.Value}: {ex.Message}");
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status400BadRequest,
                string.Empty, SszRestErrorCodes.SszDecodeError);
        }
        catch (Exception ex)
        {
            Metrics.SszRestRequestsServerErrorTotal++;
            if (_logger.IsError) _logger.Error($"SSZ-REST handler error for {ctx.Request.Path.Value}", ex);

            // If the inner code already aborted the request, writing a 500 would throw a duplicate OperationCanceledException.
            if (!ctx.RequestAborted.IsCancellationRequested)
                await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status500InternalServerError, "Internal server error");
        }
        finally
        {
            if (bodyRead) reader.AdvanceTo(body.End);
        }
    }

    private static bool TryRoute(string path, out int version, out string? fork,
        out ReadOnlyMemory<char> pathSegment, out bool unsupportedFork)
    {
        version = 1;
        fork = null;
        pathSegment = default;
        unsupportedFork = false;

        ReadOnlySpan<char> span = path.AsSpan();
        if (!span.StartsWith(EnginePrefix.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;

        // Collapse a trailing slash so `/foo` and `/foo/` route identically; pathLen bounds every later slice so the slash never reaches `extra`.
        int pathLen = path.Length;
        if (pathLen > EnginePrefix.Length && path[pathLen - 1] == '/')
        {
            pathLen--;
            span = span[..pathLen];
        }

        int offset = EnginePrefix.Length;
        span = span[offset..];
        if (span.IsEmpty) return false;

        if (span.Equals("identity".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.Equals("capabilities".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            pathSegment = path.AsMemory(offset, pathLen - offset);
            return true;
        }
        // Unscoped endpoints don't accept path extras — reject "/identity/foo" / "/capabilities/foo" as 404.
        if (span.StartsWith("identity/".AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.StartsWith("capabilities/".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (span.StartsWith("blobs/".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            ReadOnlySpan<char> sub = span["blobs/".Length..];
            if (sub.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(sub[1..], out int blobVer))
                {
                    version = blobVer;
                    pathSegment = path.AsMemory(offset, "blobs".Length);
                    return true;
                }
            }
            return false;
        }

        // Fork-scoped path: /{fork}/{resource}[/{extra}]
        int nextSlash = span.IndexOf('/');
        if (nextSlash <= 0)
        {
            nextSlash = span.Length;
        }

        ReadOnlySpan<char> forkSpan = span[..nextSlash];
        if (!SszRestPaths.SupportedForksSpanLookup.Contains(forkSpan))
        {
            // Reject `/engine/v2/v<N>/...` as 404 rather than an unsupported-fork error.
            if (forkSpan.Length > 1
                && (forkSpan[0] == 'v' || forkSpan[0] == 'V')
                && int.TryParse(forkSpan[1..], out _))
            {
                return false;
            }

            fork = forkSpan.ToString();
            unsupportedFork = true;
            return false;
        }

        fork = forkSpan.ToString();
        if (nextSlash < span.Length)
        {
            offset += nextSlash + 1;
            pathSegment = path.AsMemory(offset, pathLen - offset);
        }
        else
        {
            // Recognised fork but missing resource segment (e.g. /engine/v2/cancun) — 404, not unsupported-fork.
            return false;
        }
        return true;
    }

    private bool TryResolveHandler(string method, ReadOnlyMemory<char> pathSegment, int version, string? fork,
        out ISszEndpointHandler? handler, out ReadOnlyMemory<char> extra)
    {
        handler = null;
        extra = default;

        bool isPost = HttpMethods.IsPost(method);
        bool isGet = !isPost && HttpMethods.IsGet(method);
        if (!isPost && !isGet) return false;

        ReadOnlyMemory<char> resource = pathSegment;
        ReadOnlyMemory<char> extraMem = default;

        // Multi-segment resources ("payloads/witness", "bodies/hash") stay whole;
        // anything else splits into resource + path extra ("payloads/{id}")
        if (!_multiSegmentLookup.Contains(pathSegment.Span))
        {
            int firstSlash = pathSegment.Span.IndexOf('/');
            if (firstSlash > 0)
            {
                resource = pathSegment[..firstSlash];
                extraMem = pathSegment[(firstSlash + 1)..];
            }
        }

        if (fork is not null)
        {
            int? mappedVersion = SszRestPaths.MapForkToVersion(fork, resource.Span, method);
            if (mappedVersion is null) return false;
            version = mappedVersion.Value;
        }

        FrozenDictionary<string, List<ISszEndpointHandler>>.AlternateLookup<ReadOnlySpan<char>>
            lookup = isPost ? _postLookup : _getLookup;

        if (lookup.TryGetValue(resource.Span, out List<ISszEndpointHandler>? exactList))
        {
            ISszEndpointHandler? fallback = null;
            foreach (ISszEndpointHandler candidate in exactList)
            {
                if (candidate.Version == version)
                {
                    if (!extraMem.IsEmpty && !candidate.AcceptsPathExtra)
                        return false;

                    handler = candidate;
                    extra = extraMem;
                    return true;
                }
                if (candidate.Version is null) fallback = candidate;
            }
            if (fallback is not null)
            {
                if (!extraMem.IsEmpty && !fallback.AcceptsPathExtra)
                    return false;

                handler = fallback;
                extra = extraMem;
                return true;
            }
        }

        return false;
    }


    private SszRequestKind ClassifySszRequest(HttpContext ctx)
    {
        string path = ctx.Request.Path.Value ?? string.Empty;

        if (!path.StartsWith("/engine/", StringComparison.OrdinalIgnoreCase))
            return SszRequestKind.NotEngine;

        ReadOnlySpan<char> span = path.AsSpan("/engine/".Length);
        int nextSlash = span.IndexOf('/');
        ReadOnlySpan<char> versionSegment = nextSlash < 0 ? span : span[..nextSlash];

        bool isVersioned = versionSegment.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            && versionSegment.Length > 1
            && int.TryParse(versionSegment[1..], out _);

        if (!isVersioned)
            return SszRequestKind.NotEngine;

        bool isEnginePrefix = path.StartsWith(EnginePrefix, StringComparison.OrdinalIgnoreCase);

        switch (ctx.Request.Method)
        {
            case "POST":
                if (!isEnginePrefix) return SszRequestKind.EngineOk;
                return ctx.Request.ContentType?.Contains(
                    MediaTypeNames.Application.Octet, StringComparison.OrdinalIgnoreCase) == true
                    ? SszRequestKind.EngineOk
                    : SszRequestKind.EngineWrongMediaType;

            case "GET":
                if (!isEnginePrefix) return SszRequestKind.EngineOk;
                if (IsDiagnosticGetPath(path))
                    return SszRequestKind.EngineOk;

                foreach (string? v in ctx.Request.Headers.Accept)
                {
                    if (v is not null && v.Contains(
                        MediaTypeNames.Application.Octet, StringComparison.OrdinalIgnoreCase))
                        return SszRequestKind.EngineOk;
                }

                return SszRequestKind.NotEngine;

            default:
                return SszRequestKind.NotEngine;
        }
    }

    private static bool IsDiagnosticGetPath(string path)
    {
        ReadOnlySpan<char> span = path.AsSpan();
        const string capabilitiesPath = "/engine/v2/capabilities";
        const string identityPath = "/engine/v2/identity";

        return span.Equals(capabilitiesPath.AsSpan(), StringComparison.OrdinalIgnoreCase)
            || (span.StartsWith(capabilitiesPath.AsSpan(), StringComparison.OrdinalIgnoreCase) && span.Length > capabilitiesPath.Length && span[capabilitiesPath.Length] == '/')
            || span.Equals(identityPath.AsSpan(), StringComparison.OrdinalIgnoreCase)
            || (span.StartsWith(identityPath.AsSpan(), StringComparison.OrdinalIgnoreCase) && span.Length > identityPath.Length && span[identityPath.Length] == '/');
    }

    /// <summary>
    /// Returns the request body as a <see cref="ReadOnlySequence{T}"/> over the PipeReader's pooled segments.
    /// The caller MUST call <see cref="PipeReader.AdvanceTo(SequencePosition)"/> once the wire object has been decoded.
    /// </summary>
    private static async Task<ReadOnlySequence<byte>> ReadBodyAsync(HttpContext ctx, PipeReader reader)
    {
        long? contentLength = ctx.Request.ContentLength;
        switch (contentLength)
        {
            case > MaxBodySize:
                throw new InvalidOperationException($"Request body too large: {contentLength} bytes exceeds limit of {MaxBodySize}");

            case > 0:
                {
                    int len = (int)contentLength;
                    ReadResult rr = await reader.ReadAtLeastAsync(len, ctx.RequestAborted);
                    if (rr.Buffer.Length < len)
                        throw new EndOfStreamException($"Expected {len} bytes but stream ended with {rr.Buffer.Length}");
                    // Slice to ContentLength: keep-alive can pack the next request's framing into the same buffer.
                    return rr.Buffer.Slice(0, len);
                }
        }

        // ContentLength unknown (chunked): drain without consuming so the final ReadResult holds the whole body.
        while (true)
        {
            ReadResult rr = await reader.ReadAsync(ctx.RequestAborted);
            switch (rr)
            {
                case { Buffer.Length: > MaxBodySize }:
                    throw new InvalidOperationException($"Request body too large: exceeds limit of {MaxBodySize}");

                case { IsCompleted: true }:
                    return rr.Buffer;

                default:
                    reader.AdvanceTo(rr.Buffer.Start, rr.Buffer.End);
                    break;
            }
        }
    }
}
