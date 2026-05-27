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

    private const string EnginePrefix = "/engine/v";
    private const string WitnessPath = "/new-payload-with-witness";

    /// <summary>MAX_REQUEST_BODY_SIZE per execution-apis#764 (16 MiB).</summary>
    public const int MaxBodySize = 0x1000000;

    private readonly FrozenDictionary<string, List<ISszEndpointHandler>> _postRoutes;
    private readonly FrozenDictionary<string, List<ISszEndpointHandler>> _getRoutes;
    private readonly FrozenDictionary<string, List<ISszEndpointHandler>>.AlternateLookup<ReadOnlySpan<char>> _postLookup;
    private readonly FrozenDictionary<string, List<ISszEndpointHandler>>.AlternateLookup<ReadOnlySpan<char>> _getLookup;

    private readonly (string Resource, List<ISszEndpointHandler> Handlers)[] _postPrefixRoutes;
    private readonly (string Resource, List<ISszEndpointHandler> Handlers)[] _getPrefixRoutes;

    private readonly ISszEndpointHandler? _witnessHandler;

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
            // The witness handler is injected directly and dispatched via its own fast-path.
            if (h is NewPayloadWithWitnessSszHandler) continue;

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

        return (post, get, BuildPrefix(postDict), BuildPrefix(getDict));

        static (string Resource, List<ISszEndpointHandler> Handlers)[] BuildPrefix(
            Dictionary<string, List<ISszEndpointHandler>> source)
        {
            List<(string, List<ISszEndpointHandler>)> prefix = [];
            foreach ((string r, List<ISszEndpointHandler> list) in source)
            {
                List<ISszEndpointHandler> accepting = list.FindAll(static c => c.AcceptsPathExtra);
                if (accepting.Count > 0) prefix.Add((r, accepting));
            }
            return prefix.ToArray();
        }
    }

    public Task InvokeAsync(HttpContext ctx)
    {
        if (!IsSszRequest(ctx))
        {
            return _next(ctx);
        }

        if (_processExitToken.IsCancellationRequested)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return Task.CompletedTask;
        }

        if (!_urlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl? url) || !url.IsAuthenticated || !url.RpcEndpoint.HasFlag(RpcEndpoint.Http))
        {
            return _next(ctx);
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
                "Authentication error", ErrorCodes.InvalidRequest);
        }
        else if (IsWitnessPath(ctx.Request.Path.Value ?? string.Empty))
        {
            await DispatchWitnessAsync(ctx);
        }
        else if (!TryRoute(ctx.Request.Path.Value ?? string.Empty, out int version, out ReadOnlyMemory<char> pathSegment))
        {
            Metrics.SszRestRequestsClientErrorTotal++;
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status404NotFound,
                "Unknown SSZ endpoint", ErrorCodes.MethodNotFound);
        }
        else if (!TryResolveHandler(ctx.Request.Method, pathSegment, version, out ISszEndpointHandler? handler, out ReadOnlyMemory<char> extra))
        {
            Metrics.SszRestRequestsClientErrorTotal++;
            // .Span avoids the extra ROM<char>.ToString() allocation in the interpolation.
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status404NotFound,
                $"Unknown method: {ctx.Request.Method} /engine/v{version}/{pathSegment.Span}", ErrorCodes.MethodNotFound);
        }
        else
        {
            if (_logger.IsTrace) _logger.Trace(extra.IsEmpty
                ? $"SSZ-REST {ctx.Request.Method} /engine/v{version}/{handler!.Resource}"
                : $"SSZ-REST {ctx.Request.Method} /engine/v{version}/{handler!.Resource}/{extra.Span}");

            await DispatchAsync(ctx, handler!, version, extra);
        }
    }

    private static bool IsWitnessPath(string path)
        => path.Equals(WitnessPath, StringComparison.OrdinalIgnoreCase);

    private async Task DispatchWitnessAsync(HttpContext ctx)
    {
        if (!string.Equals(ctx.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            Metrics.SszRestRequestsClientErrorTotal++;
            ctx.Response.Headers.Allow = "POST";
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status405MethodNotAllowed,
                $"Method '{ctx.Request.Method}' is not allowed on {WitnessPath}. Only POST is supported.", ErrorCodes.MethodNotFound);
            return;
        }

        string? contentType = ctx.Request.ContentType;
        if (contentType is null || !contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
        {
            Metrics.SszRestRequestsClientErrorTotal++;
            ctx.Response.Headers["Accept"] = "application/json";
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status415UnsupportedMediaType,
                $"Content-Type must be application/json for {WitnessPath}.", ErrorCodes.ParseError);
            return;
        }

        if (_witnessHandler is null)
        {
            Metrics.SszRestRequestsClientErrorTotal++;
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status404NotFound,
                "Endpoint not available", ErrorCodes.MethodNotFound);
            return;
        }

        if (_logger.IsTrace) _logger.Trace($"SSZ-REST POST {WitnessPath}");

        await DispatchAsync(ctx, _witnessHandler, version: 0, extra: default);
    }

    /// <summary>Shared body-read + handler invocation + metrics + error mapping for both routes.</summary>
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

            switch (ctx.Response.StatusCode)
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
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status413PayloadTooLarge, ex.Message, ErrorCodes.ParseError);
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or EndOfStreamException)
        {
            // Malformed SSZ → 400 per execution-apis#764. 422 is reserved for InvalidPayloadAttributes.
            Metrics.SszRestDecodeFailuresTotal++;
            Metrics.SszRestRequestsClientErrorTotal++;
            if (_logger.IsDebug) _logger.Debug($"SSZ-REST malformed body at {ctx.Request.Path.Value}: {ex.Message}");
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "Malformed SSZ body", ErrorCodes.ParseError);
        }
        catch (Exception ex)
        {
            Metrics.SszRestRequestsServerErrorTotal++;
            if (_logger.IsError) _logger.Error($"SSZ-REST handler error for {ctx.Request.Path.Value}", ex);

            // Skip the 500 write if the handler already aborted (writing would throw OCE and log twice).
            if (!ctx.RequestAborted.IsCancellationRequested)
                await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status500InternalServerError, "Internal server error", ErrorCodes.InternalError);
        }
        finally
        {
            if (bodyRead) reader.AdvanceTo(body.End);
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

        // Allowed segment chars: ASCII alphanumeric, '-' (kebab-case resources), '/' (resource/extra
        // separator). Reject '//' in the same pass to fail malformed paths at one point.
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

        // Zero-alloc slice — backing string lives as long as the request.
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

            if (pathSpan.Length <= resourceSpan.Length || pathSpan[resourceSpan.Length] != '/')
                continue;
            if (!pathSpan.StartsWith(resourceSpan, StringComparison.OrdinalIgnoreCase))
                continue;

            ReadOnlyMemory<char> tail = pathSegment[(resourceSpan.Length + 1)..];

            foreach (ISszEndpointHandler candidate in candidates)
            {
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

    /// <summary>
    /// Determines whether this middleware should handle the incoming request.
    /// </summary>
    /// <remarks>
    /// The witness endpoint (<c>/new-payload-with-witness</c>) is intercepted for ALL HTTP
    /// methods — not just POST — so that non-POST requests receive a proper <c>405 Method Not
    /// Allowed</c> from <see cref="DispatchWitnessAsync"/> rather than falling through to the
    /// next middleware and returning a confusing 404.
    ///
    /// For a valid POST to the witness path, the request <c>Content-Type</c> must be
    /// <c>application/json</c>. For any other method, <see cref="IsSszRequest"/> returns
    /// <c>true</c> (so the middleware intercepts it) but without inspecting the Content-Type —
    /// <see cref="DispatchWitnessAsync"/> will immediately reject it with 405.
    /// </remarks>
    private static bool IsSszRequest(HttpContext ctx)
    {
        string path = ctx.Request.Path.Value ?? string.Empty;

        // Non-versioned witness endpoint — intercept all methods so we can return 405 for
        // non-POST instead of falling through and returning a confusing 404.
        if (path.Equals(WitnessPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

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
