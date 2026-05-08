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

    // Path: /engine/v{N}/{resource}[/{extra}]
    private const string EnginePrefix = "/engine/v";

    /// <summary>
    /// Maximum allowed request body size in bytes (16 MiB).
    /// Corresponds to <c>MAX_REQUEST_BODY_SIZE</c> defined in the Engine API SSZ-REST spec
    /// (see https://github.com/ethereum/execution-apis/pull/764)
    /// </summary>
    public const int MaxBodySize = 0x1000000;

    private readonly FrozenDictionary<string, List<ISszEndpointHandler>> _postRoutes;
    private readonly FrozenDictionary<string, List<ISszEndpointHandler>> _getRoutes;
    private readonly FrozenDictionary<string, List<ISszEndpointHandler>>.AlternateLookup<ReadOnlySpan<char>> _postLookup;
    private readonly FrozenDictionary<string, List<ISszEndpointHandler>>.AlternateLookup<ReadOnlySpan<char>> _getLookup;

    private readonly (string Resource, List<ISszEndpointHandler> Handlers)[] _postPrefixRoutes;
    private readonly (string Resource, List<ISszEndpointHandler> Handlers)[] _getPrefixRoutes;

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
        (_postRoutes, _getRoutes, _postPrefixRoutes, _getPrefixRoutes) = BuildRoutes(handlers);
        _postLookup = _postRoutes.GetAlternateLookup<ReadOnlySpan<char>>();
        _getLookup = _getRoutes.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    private static (FrozenDictionary<string, List<ISszEndpointHandler>> post,
                    FrozenDictionary<string, List<ISszEndpointHandler>> get,
                    (string, List<ISszEndpointHandler>)[] postPrefix,
                    (string, List<ISszEndpointHandler>)[] getPrefix)
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

        List<(string, List<ISszEndpointHandler>)> postPrefix = [];
        List<(string, List<ISszEndpointHandler>)> getPrefix = [];
        foreach ((string r, List<ISszEndpointHandler> list) in post) postPrefix.Add((r, list));
        foreach ((string r, List<ISszEndpointHandler> list) in get) getPrefix.Add((r, list));
        return (post, get, postPrefix.ToArray(), getPrefix.ToArray());
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!IsSszRequest(ctx))
        {
            await _next(ctx);
        }
        else if (_processExitToken.IsCancellationRequested)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
        else if (!_urlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl? url) || !url.IsAuthenticated)
        {
            await _next(ctx);
        }
        else
        {
            Metrics.SszRestRequestsTotal++;

            string? authHeader = ctx.Request.Headers.Authorization;
            if (authHeader is null || !await _auth.Authenticate(authHeader))
            {
                Metrics.SszRestRequestsClientErrorTotal++;
                await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status401Unauthorized,
                    "Authentication error");
            }
            else if (!TryRoute(ctx.Request.Path.Value ?? string.Empty, out int version, out ReadOnlyMemory<char> pathSegment))
            {
                Metrics.SszRestRequestsClientErrorTotal++;
                await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status404NotFound,
                    "Unknown SSZ endpoint");
            }
            else if (!TryResolveHandler(ctx.Request.Method, pathSegment, version, out ISszEndpointHandler? handler, out ReadOnlyMemory<char> extra))
            {
                Metrics.SszRestRequestsClientErrorTotal++;
                // Use .Span in the interpolation: ROM<char>.ToString() would allocate a separate
                // intermediate string; appending the span goes straight into the format buffer.
                await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status404NotFound,
                    $"Unknown method: {ctx.Request.Method} /engine/v{version}/{pathSegment.Span}");
            }
            else
            {
                if (_logger.IsTrace)
                {
                    _logger.Trace(extra.IsEmpty
                        ? $"SSZ-REST {ctx.Request.Method} /engine/v{version}/{handler!.Resource}"
                        : $"SSZ-REST {ctx.Request.Method} /engine/v{version}/{handler!.Resource}/{extra.Span}");
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
                catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or EndOfStreamException)
                {
                    // Per execution-apis #764 (Engine API SSZ Transport spec, "HTTP status codes" section):
                    // malformed SSZ encoding is 400 Bad Request. 422 Unprocessable Entity is reserved
                    // for "Invalid payload attributes" and is emitted by the handler chain via
                    // ErrorCodeToHttpStatus when the engine module returns InvalidPayloadAttributes.
                    Metrics.SszRestDecodeFailuresTotal++;
                    Metrics.SszRestRequestsClientErrorTotal++;
                    if (_logger.IsDebug) _logger.Debug($"SSZ-REST malformed body at {ctx.Request.Path.Value}: {ex.Message}");
                    await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "Malformed SSZ body");
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
    }

    private static bool TryRoute(string path, out int version, out ReadOnlyMemory<char> pathSegment)
    {
        version = 0;
        pathSegment = default;

        ReadOnlySpan<char> span = path.AsSpan();
        if (!span.StartsWith(EnginePrefix.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;

        int offset = EnginePrefix.Length;
        span = span[offset..];

        int slashPos = span.IndexOf('/');
        if (slashPos <= 0) return false;

        if (!int.TryParse(span[..slashPos], out version))
            return false;

        offset += slashPos + 1;
        span = span[(slashPos + 1)..];
        if (span.IsEmpty) return false;

        // Allowed path-segment chars: ASCII alphanumeric, '-' (kebab-case resources),
        // and '/' (between resource and extra). Reject runs of '/' in the same pass —
        // saves an extra scan and gives one rejection point for both validations.
        bool prevSlash = false;
        foreach (char c in span)
        {
            if (c == '/')
            {
                if (prevSlash) return false;
                prevSlash = true;
                continue;
            }
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                return false;
            prevSlash = false;
        }

        // Slice into the original path string — zero-allocation; the memory stays valid
        // for the lifetime of the request because Path.Value is held by ctx.Request.
        pathSegment = path.AsMemory(offset);
        return true;
    }

    private bool TryResolveHandler(string method, ReadOnlyMemory<char> pathSegment, int version,
        out ISszEndpointHandler? handler, out ReadOnlyMemory<char> extra)
    {
        handler = null;
        extra = default;

        bool isPost = HttpMethods.IsPost(method);
        bool isGet = !isPost && HttpMethods.IsGet(method);

        FrozenDictionary<string, List<ISszEndpointHandler>>? exactDict = isPost ? _postRoutes : isGet ? _getRoutes : null;

        if (exactDict is not null)
        {
            FrozenDictionary<string, List<ISszEndpointHandler>>.AlternateLookup<ReadOnlySpan<char>>
                lookup = isPost ? _postLookup : _getLookup;

            if (lookup.TryGetValue(pathSegment.Span, out List<ISszEndpointHandler>? exactList))
            {
                ISszEndpointHandler? fallback = null;
                foreach (ISszEndpointHandler candidate in exactList)
                {
                    if (candidate.Version == version) { handler = candidate; extra = default; return true; }
                    if (candidate.Version is null) fallback = candidate;
                }
                if (fallback is not null) { handler = fallback; extra = default; return true; }
            }
        }

        ISszEndpointHandler? prefixFallback = null;
        ReadOnlyMemory<char> prefixFallbackExtra = default;

        (string Resource, List<ISszEndpointHandler> Handlers)[] prefixRoutes =
            isPost ? _postPrefixRoutes : isGet ? _getPrefixRoutes : [];

        ReadOnlySpan<char> pathSpan = pathSegment.Span;
        foreach ((string routeResource, List<ISszEndpointHandler> candidates) in prefixRoutes)
        {
            ReadOnlySpan<char> resourceSpan = routeResource.AsSpan();

            if (pathSpan.Equals(resourceSpan, StringComparison.OrdinalIgnoreCase))
                continue;
            if (pathSpan.Length <= resourceSpan.Length || pathSpan[resourceSpan.Length] != '/')
                continue;
            if (!pathSpan.StartsWith(resourceSpan, StringComparison.OrdinalIgnoreCase))
                continue;

            ReadOnlyMemory<char> tail = pathSegment[(resourceSpan.Length + 1)..];

            foreach (ISszEndpointHandler candidate in candidates)
            {
                if (!candidate.AcceptsPathExtra) continue;

                if (candidate.Version == version)
                {
                    handler = candidate;
                    extra = tail;
                    return true;
                }

                if (candidate.Version is null)
                {
                    prefixFallback = candidate;
                    prefixFallbackExtra = tail;
                }
            }
        }

        if (prefixFallback is not null)
        {
            handler = prefixFallback;
            extra = prefixFallbackExtra;
            return true;
        }

        return false;
    }

    private static bool IsSszRequest(HttpContext ctx)
    {
        string path = ctx.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/engine/", StringComparison.OrdinalIgnoreCase))
            return false;

        switch (ctx.Request.Method)
        {
            case "POST":
                return ctx.Request.ContentType?.Contains(MediaTypeNames.Application.Octet, StringComparison.OrdinalIgnoreCase) == true;
            case "GET":
                {
                    foreach (string? v in ctx.Request.Headers.Accept)
                    {
                        if (v is not null && v.Contains(MediaTypeNames.Application.Octet, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }

                    return false;
                }
            default:
                return false;
        }
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
                    // Slice to the declared ContentLength even if Kestrel buffered extra bytes
                    // (HTTP keep-alive could carry the next request's framing in the same buffer).
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
