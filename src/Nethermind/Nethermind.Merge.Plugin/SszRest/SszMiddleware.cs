// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Mime;
using System.Text.RegularExpressions;
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
    private static readonly Regex PathRegex = new(
        @"^/engine/v(?<version>\d+)/(?<resource>[a-z][a-z\-]*(?:/[a-z][a-z\-]*)*)(?:/(?<extra>[^/]+))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        if (!TryRoute(ctx.Request.Path.Value ?? string.Empty, out int version, out string resource, out string extra))
        {
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status404NotFound, "Unknown SSZ endpoint");
            return;
        }

        if (_logger.IsTrace)
            _logger.Trace($"SSZ-REST {ctx.Request.Method} /engine/v{version}/{resource}" +
                          (extra.Length > 0 ? "/" + extra : ""));

        if (!TryResolveHandler(ctx.Request.Method, resource, version, out ISszEndpointHandler? handler))
        {
            await SszEndpointHandlerBase.WriteErrorAsync(ctx, StatusCodes.Status404NotFound,
                $"Unknown method: {ctx.Request.Method} /engine/v{version}/{resource}");
            return;
        }

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

    private static bool TryRoute(string path, out int version, out string resource, out string extra)
    {
        version = 0;
        resource = string.Empty;
        extra = string.Empty;

        Match match = PathRegex.Match(path);
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups["version"].Value, out version)) return false;

        resource = match.Groups["resource"].Value.ToLowerInvariant();
        extra = match.Groups["extra"].Value;
        return true;
    }

    private bool TryResolveHandler(string method, string resource, int version, out ISszEndpointHandler? handler)
    {
        if (!_routes.TryGetValue((method.ToLowerInvariant(), resource), out List<ISszEndpointHandler>? candidates))
        {
            handler = null;
            return false;
        }
        ISszEndpointHandler? fallback = null;

        foreach (ISszEndpointHandler candidate in candidates)
        {
            if (candidate.Version == version)
            {
                handler = candidate;
                return true;
            }
            if (candidate.Version is null)
                fallback = candidate;
        }

        handler = fallback;
        return handler is not null;
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
        if (ctx.Request.ContentLength > MaxBodySize)
            throw new InvalidOperationException(
                $"Request body too large: {ctx.Request.ContentLength} bytes exceeds limit of {MaxBodySize}");

        using MemoryStream ms = new();
        byte[] rent = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await ctx.Request.Body.ReadAsync(rent, ctx.RequestAborted)) > 0)
            {
                if (ms.Length + read > MaxBodySize)
                    throw new InvalidOperationException(
                        $"Request body too large: exceeds limit of {MaxBodySize}");
                ms.Write(rent, 0, read);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(rent); }

        return ms.ToArray();
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
