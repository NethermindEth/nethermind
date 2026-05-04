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

    private readonly (string Method, string Resource, List<ISszEndpointHandler> Handlers)[] _allRoutes;

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
        (_postRoutes, _getRoutes, _allRoutes) = BuildRoutes(handlers);
    }

    private static (FrozenDictionary<string, List<ISszEndpointHandler>> post,
                    FrozenDictionary<string, List<ISszEndpointHandler>> get,
                    (string, string, List<ISszEndpointHandler>)[] all)
        BuildRoutes(IEnumerable<ISszEndpointHandler> handlers)
    {
        Dictionary<string, List<ISszEndpointHandler>> postDict = [];
        Dictionary<string, List<ISszEndpointHandler>> getDict = [];

        foreach (ISszEndpointHandler h in handlers)
        {
            string resource = h.Resource.ToLowerInvariant();
            Dictionary<string, List<ISszEndpointHandler>> dict =
                h.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase) ? getDict : postDict;
            if (!dict.TryGetValue(resource, out List<ISszEndpointHandler>? list))
                dict[resource] = list = [];
            list.Add(h);
        }

        FrozenDictionary<string, List<ISszEndpointHandler>> post = postDict.ToFrozenDictionary();
        FrozenDictionary<string, List<ISszEndpointHandler>> get = getDict.ToFrozenDictionary();

        List<(string, string, List<ISszEndpointHandler>)> all = [];
        foreach ((string r, List<ISszEndpointHandler> list) in post) all.Add(("post", r, list));
        foreach ((string r, List<ISszEndpointHandler> list) in get) all.Add(("get", r, list));
        return (post, get, all.ToArray());
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!IsSszRequest(ctx))
        {
            await _next(ctx);
            return;
        }

        if (_processExitToken.IsCancellationRequested)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        if (!_urlCollection.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl? url) || !url.IsAuthenticated)
        {
            await _next(ctx);
            return;
        }

        string? authHeader = ctx.Request.Headers.Authorization;
        if (authHeader is null || !await _auth.Authenticate(authHeader))
        {
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status401Unauthorized, "Authentication error");
            return;
        }

        if (!TryRoute(ctx.Request.Path.Value ?? string.Empty, out int version, out ReadOnlySpan<char> pathSegment))
        {
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status404NotFound, "Unknown SSZ endpoint");
            return;
        }

        if (!TryResolveHandler(ctx.Request.Method, pathSegment, version, out ISszEndpointHandler? handler, out ReadOnlySpan<char> extraSpan))
        {
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status404NotFound,
                $"Unknown method: {ctx.Request.Method} /engine/v{version}/{pathSegment}");
            return;
        }

        string extra = extraSpan.IsEmpty ? string.Empty : extraSpan.ToString();

        if (_logger.IsTrace)
            _logger.Trace($"SSZ-REST {ctx.Request.Method} /engine/v{version}/{handler!.Resource}" +
                          (extra.Length > 0 ? "/" + extra : ""));

        byte[]? rentedBuffer = null;
        int bodyLength = 0;
        try
        {
            (rentedBuffer, bodyLength) = await ReadBodyAsync(ctx);
            ReadOnlyMemory<byte> bodyMemory = rentedBuffer is null
                ? ReadOnlyMemory<byte>.Empty
                : rentedBuffer.AsMemory(0, bodyLength);

            await handler!.HandleAsync(ctx, version, extra, bodyMemory);
        }
        catch (InvalidOperationException ex) when (rentedBuffer is null)
        {
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status413PayloadTooLarge, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException)
        {
            if (_logger.IsDebug) _logger.Debug($"SSZ-REST malformed body at {ctx.Request.Path.Value}: {ex.Message}");
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status422UnprocessableEntity, "Malformed SSZ body");
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"SSZ-REST handler error for {ctx.Request.Path.Value}", ex);
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status500InternalServerError, "Internal server error");
        }
        finally
        {
            if (rentedBuffer is not null) ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    private static bool TryRoute(string path, out int version, out ReadOnlySpan<char> pathSegment)
    {
        version = 0;
        pathSegment = default;

        ReadOnlySpan<char> span = path.AsSpan();
        if (!span.StartsWith(EnginePrefix.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;

        span = span[EnginePrefix.Length..];

        int slashPos = span.IndexOf('/');
        if (slashPos <= 0) return false;

        if (!int.TryParse(span[..slashPos], out version))
            return false;

        span = span[(slashPos + 1)..];
        if (span.IsEmpty) return false;

        // Path segments may contain ASCII alphanumerics, hyphens (kebab-case resource names),
        // forward slashes (extra path segments), and the '0x' hex-prefix character 'x'.
        foreach (char c in span)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '/')
                return false;
        }

        if (span.Contains("//", StringComparison.Ordinal))
            return false;

        pathSegment = span;
        return true;
    }

    private bool TryResolveHandler(string method, ReadOnlySpan<char> pathSegment, int version,
        out ISszEndpointHandler? handler, out ReadOnlySpan<char> extra)
    {
        handler = null;
        extra = default;

        bool isPost = method is "POST" || method.Equals("post", StringComparison.OrdinalIgnoreCase);
        bool isGet = !isPost && (method is "GET" || method.Equals("get", StringComparison.OrdinalIgnoreCase));

        FrozenDictionary<string, List<ISszEndpointHandler>>? exactDict =
            isPost ? _postRoutes : isGet ? _getRoutes : null;

        if (exactDict is not null)
        {
            FrozenDictionary<string, List<ISszEndpointHandler>>.AlternateLookup<ReadOnlySpan<char>>
                lookup = exactDict.GetAlternateLookup<ReadOnlySpan<char>>();

            if (lookup.TryGetValue(pathSegment, out List<ISszEndpointHandler>? exactList))
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

        ReadOnlySpan<char> methodSpan = method.AsSpan();
        ISszEndpointHandler? prefixFallback = null;
        ReadOnlySpan<char> prefixFallbackExtra = default;

        foreach ((string routeMethod, string routeResource, List<ISszEndpointHandler> candidates) in _allRoutes)
        {
            if (!methodSpan.Equals(routeMethod.AsSpan(), StringComparison.OrdinalIgnoreCase)) continue;

            ReadOnlySpan<char> resourceSpan = routeResource.AsSpan();

            if (MemoryExtensions.Equals(pathSegment, resourceSpan, StringComparison.OrdinalIgnoreCase))
                continue;

            if (pathSegment.Length <= resourceSpan.Length || pathSegment[resourceSpan.Length] != '/')
                continue;
            if (!pathSegment.StartsWith(resourceSpan, StringComparison.OrdinalIgnoreCase))
                continue;

            ReadOnlySpan<char> tailSpan = pathSegment[(resourceSpan.Length + 1)..];

            foreach (ISszEndpointHandler candidate in candidates)
            {
                if (!candidate.AcceptsPathExtra) continue;

                if (candidate.Version == version)
                {
                    handler = candidate;
                    extra = tailSpan;
                    return true;
                }

                if (candidate.Version is null)
                {
                    prefixFallback = candidate;
                    prefixFallbackExtra = tailSpan;
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

        if (ctx.Request.Method is "POST")
            return ctx.Request.ContentType?.Contains(MediaTypeNames.Application.Octet, StringComparison.OrdinalIgnoreCase) == true;

        if (ctx.Request.Method == "GET")
        {
            foreach (string? v in ctx.Request.Headers.Accept)
                if (v is not null && v.Contains(MediaTypeNames.Application.Octet, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    private static async Task<(byte[]? buffer, int length)> ReadBodyAsync(HttpContext ctx)
    {
        long? contentLength = ctx.Request.ContentLength;
        if (contentLength > MaxBodySize)
            throw new InvalidOperationException(
                $"Request body too large: {contentLength} bytes exceeds limit of {MaxBodySize}");

        if (contentLength is > 0)
        {
            int len = (int)contentLength;
            byte[] rent = ArrayPool<byte>.Shared.Rent(len);
            await ctx.Request.Body.ReadExactlyAsync(rent.AsMemory(0, len), ctx.RequestAborted);
            return (rent, len);
        }

        PipeReader reader = ctx.Request.BodyReader;
        byte[]? result = null;
        int written = 0;
        try
        {
            while (true)
            {
                ReadResult rr = await reader.ReadAsync(ctx.RequestAborted);
                ReadOnlySequence<byte> seq = rr.Buffer;

                int needed = written + (int)seq.Length;
                if (needed > MaxBodySize)
                    throw new InvalidOperationException(
                        $"Request body too large: exceeds limit of {MaxBodySize}");

                if (needed > 0)
                {
                    if (result is null)
                        result = ArrayPool<byte>.Shared.Rent(Math.Max(needed, 4096));
                    else if (result.Length < needed)
                    {
                        byte[] larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(MaxBodySize, Math.Max(needed, (long)result.Length * 2)));
                        result.AsSpan(0, written).CopyTo(larger);
                        ArrayPool<byte>.Shared.Return(result);
                        result = larger;
                    }

                    seq.CopyTo(result.AsSpan(written));
                    written += (int)seq.Length;
                }

                reader.AdvanceTo(seq.End);

                if (rr.IsCompleted) break;
            }

            byte[]? owned = result;
            result = null;
            return (owned, written);
        }
        finally
        {
            if (result is not null) ArrayPool<byte>.Shared.Return(result);
            await reader.CompleteAsync();
        }
    }
}
