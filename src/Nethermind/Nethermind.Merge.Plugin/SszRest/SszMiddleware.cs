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
using Nethermind.Merge.Plugin.Data;

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

    // Path: /engine/{fork}/{resource}[/{extra}]
    private const string EnginePrefix = "/engine/";

    /// <summary>
    /// Maximum allowed request body size in bytes (128 MiB).
    /// Matches <c>MAX_REQUEST_BODY_SIZE</c> in the Engine API SSZ-REST spec
    /// (see https://github.com/ethereum/execution-apis/pull/793).
    /// </summary>
    public const int MaxBodySize = 0x8000000;

    private readonly FrozenDictionary<string, List<ISszEndpointHandler>> _postRoutes;
    private readonly FrozenDictionary<string, List<ISszEndpointHandler>> _getRoutes;
    private readonly FrozenDictionary<string, List<ISszEndpointHandler>>.AlternateLookup<ReadOnlySpan<char>> _postLookup;
    private readonly FrozenDictionary<string, List<ISszEndpointHandler>>.AlternateLookup<ReadOnlySpan<char>> _getLookup;

    private static readonly System.Text.Json.JsonSerializerOptions _headerJsonOptions =
        new() { PropertyNameCaseInsensitive = true };

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
    }

    private static (FrozenDictionary<string, List<ISszEndpointHandler>> post,
                    FrozenDictionary<string, List<ISszEndpointHandler>> get)
        BuildRoutes(IEnumerable<ISszEndpointHandler> handlers)
    {
        Dictionary<string, List<ISszEndpointHandler>> postDict = [];
        Dictionary<string, List<ISszEndpointHandler>> getDict = [];

        foreach (ISszEndpointHandler h in handlers)
        {
            string resource = h.Resource.ToLowerInvariant();
            Dictionary<string, List<ISszEndpointHandler>> dict =
                h.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase)
                    ? getDict
                    : postDict;

            if (!dict.TryGetValue(resource, out List<ISszEndpointHandler>? list))
                dict[resource] = list = [];

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
        if (ctx.Request.Headers.TryGetValue("X-Engine-Client-Version", out Microsoft.Extensions.Primitives.StringValues headerValues) && headerValues.Count > 0)
        {
            string? headerVal = headerValues[0];
            if (!string.IsNullOrWhiteSpace(headerVal))
            {
                try
                {
                    ClientVersionV1 clVer = System.Text.Json.JsonSerializer.Deserialize<ClientVersionV1>(headerVal, _headerJsonOptions);
                    ctx.Items["X-Engine-Client-Version"] = clVer;
                }
                catch (Exception ex)
                {
                    if (_logger.IsTrace) _logger.Trace($"SSZ-REST: ignoring malformed X-Engine-Client-Version header: {ex.Message}");
                }
            }
        }

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
            // Use .Span in the interpolation: ROM<char>.ToString() would allocate a separate
            // intermediate string; appending the span goes straight into the format buffer.
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status404NotFound,
                $"Unknown method: {ctx.Request.Method} /engine/{pathSegment.Span}",
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
                    ? $"SSZ-REST {ctx.Request.Method} /engine/{pathSegment.Span}"
                    : $"SSZ-REST {ctx.Request.Method} /engine/{pathSegment.Span}/{extra.Span}");
            }

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

                await handler!.HandleAsync(ctx, version, extra, body);

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

        if (span.EndsWith("/"))
            return false;

        int offset = EnginePrefix.Length;
        span = span[offset..];
        if (span.IsEmpty) return false;

        if (span.Equals("identity".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            pathSegment = path.AsMemory(offset);
            return true;
        }
        if (span.Equals("capabilities".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            pathSegment = path.AsMemory(offset);
            return true;
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
        string forkStr = forkSpan.ToString().ToLowerInvariant();
        // SszRestPaths.SupportedForks is the single source of truth for recognised fork names.
        if (!SszRestPaths.SupportedForks.Contains(forkStr))
        {
            if (forkStr.StartsWith("v") && forkStr.Length > 1 && int.TryParse(forkStr.AsSpan(1), out _))
            {
                return false;
            }

            fork = forkStr;
            unsupportedFork = true;
            return false;
        }

        fork = forkStr;
        if (nextSlash < span.Length)
        {
            offset += nextSlash + 1;
            pathSegment = path.AsMemory(offset);
        }
        else
        {
            // Recognised fork but missing resource segment, e.g. /engine/cancun — not
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

        string resourceStr = pathSegment.ToString();
        string extraStr = string.Empty;

        int firstSlash = resourceStr.IndexOf('/');
        if (firstSlash > 0)
        {
            extraStr = resourceStr[(firstSlash + 1)..];
            resourceStr = resourceStr[..firstSlash];
        }

        if (resourceStr.Equals("bodies", StringComparison.OrdinalIgnoreCase) && extraStr.Equals("hash", StringComparison.OrdinalIgnoreCase))
        {
            resourceStr = "bodies/hash";
            extraStr = string.Empty;
        }

        if (fork is not null)
        {
            int? mappedVersion = MapForkToVersion(fork, resourceStr, method);
            if (mappedVersion is null) return false;
            version = mappedVersion.Value;
        }

        FrozenDictionary<string, List<ISszEndpointHandler>>? exactDict = isPost ? _postRoutes : isGet ? _getRoutes : null;

        if (exactDict is not null)
        {
            FrozenDictionary<string, List<ISszEndpointHandler>>.AlternateLookup<ReadOnlySpan<char>>
                lookup = isPost ? _postLookup : _getLookup;

            if (lookup.TryGetValue(resourceStr.AsSpan(), out List<ISszEndpointHandler>? exactList))
            {
                ISszEndpointHandler? fallback = null;
                foreach (ISszEndpointHandler candidate in exactList)
                {
                    if (candidate.Version == version)
                    {
                        if (!string.IsNullOrEmpty(extraStr) && !candidate.AcceptsPathExtra)
                            return false;

                        handler = candidate;
                        extra = extraStr.AsMemory();
                        return true;
                    }
                    if (candidate.Version is null) fallback = candidate;
                }
                if (fallback is not null)
                {
                    if (!string.IsNullOrEmpty(extraStr) && !fallback.AcceptsPathExtra)
                        return false;

                    handler = fallback;
                    extra = extraStr.AsMemory();
                    return true;
                }
            }
        }

        return false;
    }

    public static int? MapForkToVersion(string fork, string resource, string httpMethod) =>
        SszRestPaths.MapForkToVersion(fork, resource, httpMethod);


    private static SszRequestKind ClassifySszRequest(HttpContext ctx)
    {
        string path = ctx.Request.Path.Value ?? string.Empty;

        if (!path.StartsWith(EnginePrefix, StringComparison.OrdinalIgnoreCase))
            return SszRequestKind.NotEngine;

        switch (ctx.Request.Method)
        {
            case "POST":
                return ctx.Request.ContentType?.Contains(
                    MediaTypeNames.Application.Octet, StringComparison.OrdinalIgnoreCase) == true
                    ? SszRequestKind.EngineOk
                    : SszRequestKind.EngineWrongMediaType;

            case "GET":
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
        const string capabilitiesPath = "/engine/capabilities";
        const string identityPath = "/engine/identity";
        return span.StartsWith(capabilitiesPath.AsSpan(), StringComparison.OrdinalIgnoreCase)
            || span.StartsWith(identityPath.AsSpan(), StringComparison.OrdinalIgnoreCase);
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
