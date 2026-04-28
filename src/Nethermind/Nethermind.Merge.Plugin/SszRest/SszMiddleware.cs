// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nethermind.Core.Authentication;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.SszRest.Handlers;

namespace Nethermind.Merge.Plugin.SszRest;

/// <summary>
/// ASP.NET Core middleware that routes binary SSZ-REST engine API calls to
/// the appropriate <see cref="ISszEndpointHandler"/>.
/// </summary>
public sealed class SszMiddleware(
    RequestDelegate next,
    IJsonRpcUrlCollection urlCollection,
    IRpcAuthentication auth,
    IEnumerable<ISszEndpointHandler> handlers,
    ISszProcessExitSource processExitSource,
    ILogManager logManager)
{
    private readonly RequestDelegate _next = next;
    private readonly IJsonRpcUrlCollection _urlCollection = urlCollection;
    private readonly IRpcAuthentication _auth = auth;
    private readonly ILogger _logger = logManager.GetClassLogger<SszMiddleware>();
    private readonly CancellationToken _processExitToken = processExitSource.ProcessExit;

    // Path: /engine/v{N}/{resource}[/{extra}]
    private const string EnginePrefix = "/engine/v";

    private const int MaxBodySize = 0x1000000;
    private readonly Dictionary<(string Method, string Resource), List<ISszEndpointHandler>> _routes = BuildRoutes(handlers);

    private static Dictionary<(string, string), List<ISszEndpointHandler>> BuildRoutes(
        IEnumerable<ISszEndpointHandler> handlers)
    {
        Dictionary<(string, string), List<ISszEndpointHandler>> dict = new();
        foreach (ISszEndpointHandler h in handlers)
        {
            (string, string) key = (h.HttpMethod.ToLowerInvariant(), h.Resource.ToLowerInvariant());
            if (!dict.TryGetValue(key, out List<ISszEndpointHandler>? list))
                dict[key] = list = [];
            list.Add(h);
        }
        return dict;
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

        if (!await _auth.Authenticate(ctx.Request.Headers.Authorization.ToString()))
        {
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status401Unauthorized, "Authentication error");
            return;
        }

        if (!TryRoute(ctx.Request.Path.Value ?? string.Empty, out int version, out string pathSegment))
        {
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status404NotFound, "Unknown SSZ endpoint");
            return;
        }

        if (!TryResolveHandler(ctx.Request.Method, pathSegment, version, out ISszEndpointHandler? handler, out string extra))
        {
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status404NotFound,
                $"Unknown method: {ctx.Request.Method} /engine/v{version}/{pathSegment}");
            return;
        }

        if (_logger.IsTrace)
            _logger.Trace($"SSZ-REST {ctx.Request.Method} /engine/v{version}/{handler!.Resource}" +
                          (extra.Length > 0 ? "/" + extra : ""));

        byte[] body;
        try
        {
            body = await ReadBodyAsync(ctx);
        }
        catch (InvalidOperationException ex)
        {
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status413PayloadTooLarge, ex.Message);
            return;
        }

        try
        {
            await handler!.HandleAsync(ctx, version, extra, body);
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"SSZ-REST handler error for {ctx.Request.Path.Value}", ex);
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status500InternalServerError, "Internal server error");
        }
    }

    private static bool TryRoute(string path, out int version, out string pathSegment)
    {
        version = 0;
        pathSegment = string.Empty;

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

        foreach (char c in span)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '/')
                return false;
        }

        pathSegment = span.ToString().ToLowerInvariant();
        return true;
    }

    private bool TryResolveHandler(string method, string pathSegment, int version,
        out ISszEndpointHandler? handler, out string extra)
    {
        handler = null;
        extra = string.Empty;
        string methodLower = method.ToLowerInvariant();

        ISszEndpointHandler? fallback = null;
        string fallbackExtra = string.Empty;

        foreach (KeyValuePair<(string Method, string Resource), List<ISszEndpointHandler>> kvp in _routes)
        {
            if (kvp.Key.Method != methodLower) continue;

            string resource = kvp.Key.Resource;

            string pathExtra;
            if (pathSegment.Length == resource.Length &&
                pathSegment.Equals(resource, StringComparison.Ordinal))
            {
                pathExtra = string.Empty;
            }
            else if (pathSegment.Length > resource.Length &&
                     pathSegment[resource.Length] == '/' &&
                     pathSegment.StartsWith(resource, StringComparison.Ordinal))
            {
                pathExtra = pathSegment[(resource.Length + 1)..];
            }
            else
            {
                continue;
            }

            foreach (ISszEndpointHandler candidate in kvp.Value)
            {
                if (candidate.Version == version)
                {
                    handler = candidate;
                    extra = pathExtra;
                    return true;
                }

                if (candidate.Version is null)
                {
                    fallback = candidate;
                    fallbackExtra = pathExtra;
                }
            }
        }

        if (fallback is not null)
        {
            handler = fallback;
            extra = fallbackExtra;
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

    private static async Task<byte[]> ReadBodyAsync(HttpContext ctx)
    {
        long? contentLength = ctx.Request.ContentLength;
        if (contentLength > MaxBodySize)
            throw new InvalidOperationException(
                $"Request body too large: {contentLength} bytes exceeds limit of {MaxBodySize}");

        if (contentLength is > 0)
        {
            byte[] exact = new byte[(int)contentLength];
            await ctx.Request.Body.ReadExactlyAsync(exact, ctx.RequestAborted);
            return exact;
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

                if (result is null)
                    result = ArrayPool<byte>.Shared.Rent(Math.Max(needed, 4096));
                else if (result.Length < needed)
                {
                    byte[] larger = ArrayPool<byte>.Shared.Rent(needed);
                    result.AsSpan(0, written).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(result);
                    result = larger;
                }

                seq.CopyTo(result.AsSpan(written));
                written += (int)seq.Length;
                reader.AdvanceTo(seq.End);

                if (rr.IsCompleted) break;
            }

            return result is null ? [] : result.AsSpan(0, written).ToArray();
        }
        finally
        {
            if (result is not null) ArrayPool<byte>.Shared.Return(result);
            await reader.CompleteAsync();
        }
    }
}

public interface ISszProcessExitSource
{
    CancellationToken ProcessExit { get; }
}

internal sealed class SszProcessExitSource : ISszProcessExitSource
{
    public required CancellationToken ProcessExit { get; init; }
}
