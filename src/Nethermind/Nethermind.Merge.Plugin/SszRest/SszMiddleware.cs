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

    // Path: /engine/v2/{fork}/{resource}[/{extra}]
    private const string EnginePrefix = "/engine/v2/";

    // The witness endpoint (EIP-7928) keeps its own dedicated, version-less path and a fast-path
    // dispatch — it is not part of the /engine/v2/{fork}/{resource} routing scheme, and it speaks
    // application/json rather than application/octet-stream.
    private const string WitnessPath = "/new-payload-with-witness";

    /// <summary>
    /// Maximum allowed request body size in bytes (64 MiB).
    /// Mirrors the <c>payload.max_bytes</c> example value advertised in the Engine API
    /// SSZ-REST spec capabilities response (see https://github.com/ethereum/execution-apis/pull/793).
    /// </summary>
    public const int MaxBodySize = 0x4000000;

    private readonly FrozenDictionary<string, List<ISszEndpointHandler>> _postRoutes;
    private readonly FrozenDictionary<string, List<ISszEndpointHandler>> _getRoutes;
    private readonly FrozenDictionary<string, List<ISszEndpointHandler>>.AlternateLookup<ReadOnlySpan<char>> _postLookup;
    private readonly FrozenDictionary<string, List<ISszEndpointHandler>>.AlternateLookup<ReadOnlySpan<char>> _getLookup;

    private readonly ISszEndpointHandler? _witnessHandler;

    private enum SszRequestKind { NotEngine, EngineWrongMediaType, EngineOk }

    public SszMiddleware(
        RequestDelegate next,
        IJsonRpcUrlCollection urlCollection,
        IRpcAuthentication auth,
        IEnumerable<ISszEndpointHandler> handlers,
        NewPayloadWithWitnessSszHandler? witnessHandler,
        IProcessExitSource processExitSource,
        ILogManager logManager)
    {
        _next = next;
        _urlCollection = urlCollection;
        _auth = auth;
        _witnessHandler = witnessHandler;
        _logger = logManager.GetClassLogger<SszMiddleware>();
        _processExitToken = processExitSource.Token;
        (_postRoutes, _getRoutes) = BuildRoutes(handlers);
        _postLookup = _postRoutes.GetAlternateLookup<ReadOnlySpan<char>>();
        _getLookup = _getRoutes.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    private static (FrozenDictionary<string, List<ISszEndpointHandler>> post,
                    FrozenDictionary<string, List<ISszEndpointHandler>> get)
        BuildRoutes(IEnumerable<ISszEndpointHandler> handlers)
    {
        Dictionary<string, List<ISszEndpointHandler>> postDict = [];
        Dictionary<string, List<ISszEndpointHandler>> getDict = [];

        foreach (ISszEndpointHandler h in handlers)
        {
            // Dictionaries are keyed case-insensitively below — keep resource as-is, no lowercasing.
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
        else if (IsWitnessPath(ctx.Request.Path.Value ?? string.Empty))
        {
            await DispatchWitnessAsync(ctx);
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
            // Use .Span in the interpolation: ROM<char>.ToString() would allocate a separate
            // intermediate string; appending the span goes straight into the format buffer.
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

    private static bool IsWitnessPath(string path)
        => path.Equals(WitnessPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fast-path dispatch for the version-less witness endpoint. Validates method (POST only) and
    /// content-type (application/json) before delegating to the witness handler via <see cref="DispatchAsync"/>.
    /// </summary>
    private async Task DispatchWitnessAsync(HttpContext ctx)
    {
        if (!HttpMethods.IsPost(ctx.Request.Method))
        {
            Metrics.SszRestRequestsClientErrorTotal++;
            ctx.Response.Headers.Allow = "POST";
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status405MethodNotAllowed,
                $"Method '{ctx.Request.Method}' is not allowed on {WitnessPath}. Only POST is supported.",
                SszRestErrorCodes.MethodNotFound);
            return;
        }

        string? contentType = ctx.Request.ContentType;
        if (contentType is null || !contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            Metrics.SszRestRequestsClientErrorTotal++;
            ctx.Response.Headers["Accept"] = "application/json";
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status415UnsupportedMediaType,
                $"Content-Type must be application/json for {WitnessPath}.",
                SszRestErrorCodes.UnsupportedMediaType);
            return;
        }

        if (_witnessHandler is null)
        {
            Metrics.SszRestRequestsClientErrorTotal++;
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status404NotFound,
                "Endpoint not available", SszRestErrorCodes.MethodNotFound);
            return;
        }

        if (_logger.IsTrace) _logger.Trace($"SSZ-REST POST {WitnessPath}");

        await DispatchAsync(ctx, _witnessHandler, version: 0, extra: default);
    }

    /// <summary>Shared body-read + handler invocation + metrics + error mapping for both the
    /// fork-routed endpoints and the witness fast-path.</summary>
    private async Task DispatchAsync(HttpContext ctx, ISszEndpointHandler handler, int version, ReadOnlyMemory<char> extra)
    {
        // Read directly from PipeReader: the buffer is a ReadOnlySequence over Kestrel's
        // pooled blocks (~4 KB each), so multi-segment is the common case for blob-bearing
        // payloads. The generated SSZ codecs accept ReadOnlySequence<byte> — single-segment
        // is zero-copy, multi-segment consolidates once via ArrayPool. Both paths skip the
        // MemoryStream + ToArray dance the previous implementation needed.
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
            // Per execution-apis #793 (Engine API SSZ Transport spec, "HTTP status codes" section):
            // malformed SSZ encoding is 400 Bad Request with type=ssz-decode-error: canned error,
            // no detail (spec verbatim).  422 Unprocessable Entity is reserved for
            // "Invalid payload attributes" and is emitted by the handler chain via
            // ErrorCodeToHttpStatus when the engine module returns InvalidPayloadAttributes.
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

            // If the inner code already aborted the request (e.g. encode failed mid-stream
            // and called ctx.Abort), don't try to write a 500 — WriteAsync would throw
            // OperationCanceledException, producing a duplicate exception in the logs.
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

        // Collapse a trailing slash so `/foo` and `/foo/` route identically; `pathLen` then
        // bounds every subsequent `path.AsMemory(...)` slice so the slash never reaches `extra`.
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
        // Unscoped endpoints don't accept path extras — reject "/identity/foo" / "/capabilities/foo"
        // as 404 method-not-found rather than letting them fall through to fork parsing.
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

        // Everything remaining should be a fork-scoped path: /{fork}/{resource}[/{extra}]
        int nextSlash = span.IndexOf('/');
        if (nextSlash <= 0)
        {
            // E.g. bodies query or payloads query without extra path segment
            nextSlash = span.Length;
        }

        ReadOnlySpan<char> forkSpan = span[..nextSlash];
        // SszRestPaths.SupportedForks uses OrdinalIgnoreCase, so no lowercasing needed.
        if (!SszRestPaths.SupportedForksSpanLookup.Contains(forkSpan))
        {
            // Reject `/engine/v2/v<N>/...` (looks like a version-only path with no fork) before
            // emitting an unsupported-fork error — let the caller produce a 404 instead.
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
            // Recognised fork but missing resource segment, e.g. /engine/v2/cancun — not
            // a valid endpoint; leave unsupportedFork = false so the caller uses 404.
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

        ReadOnlyMemory<char> resource = pathSegment;
        ReadOnlyMemory<char> extraMem = default;

        int firstSlash = pathSegment.Span.IndexOf('/');
        if (firstSlash > 0)
        {
            extraMem = pathSegment[(firstSlash + 1)..];
            resource = pathSegment[..firstSlash];
        }

        if (resource.Span.Equals("bodies".AsSpan(), StringComparison.OrdinalIgnoreCase)
            && extraMem.Span.Equals("hash".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            resource = SszRestPaths.PayloadBodiesByHash.AsMemory();
            extraMem = default;
        }

        if (fork is not null)
        {
            int? mappedVersion = SszRestPaths.MapForkToVersion(fork, resource.Span, method);
            if (mappedVersion is null) return false;
            version = mappedVersion.Value;
        }

        FrozenDictionary<string, List<ISszEndpointHandler>>? exactDict = isPost ? _postRoutes : isGet ? _getRoutes : null;

        if (exactDict is not null)
        {
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
        }

        return false;
    }


    private static SszRequestKind ClassifySszRequest(HttpContext ctx)
    {
        string path = ctx.Request.Path.Value ?? string.Empty;

        // The witness endpoint is a dedicated, version-less path. Intercept it for ALL methods so
        // non-POST / wrong content-type get a proper 405/415 from DispatchWitnessAsync rather than
        // falling through to the next middleware and returning a confusing 404. The application/json
        // content-type check is deferred to DispatchWitnessAsync (it is not an octet-stream route).
        if (path.Equals(WitnessPath, StringComparison.OrdinalIgnoreCase))
            return SszRequestKind.EngineOk;

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

                // Hot-path SSZ GET endpoints require Accept: application/octet-stream.
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
    /// Returns the request body as a <see cref="ReadOnlySequence{T}"/> over the PipeReader's
    /// pooled segments. The caller MUST call <see cref="PipeReader.AdvanceTo(SequencePosition)"/>
    /// once the wire object has been decoded (no remaining views into the segments).
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

        // ContentLength unknown (chunked transfer): drain the pipe without consuming any
        // bytes so the final ReadResult holds the entire body in one ReadOnlySequence.
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
