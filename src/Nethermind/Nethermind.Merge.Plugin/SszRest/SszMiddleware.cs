// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    private const string OctetStream = "application/octet-stream";
    private const int MaxBodySize = 16 * 1024 * 1024;
    private readonly ILookup<(string Method, string Resource), ISszEndpointHandler> _routes =
        handlers.ToLookup(
            h => (h.HttpMethod, h.Resource.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase.ToTupleComparer());

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
            await WriteErrorAsync(ctx, StatusCodes.Status401Unauthorized, "Authentication error");
            return;
        }

        string path = ctx.Request.Path.Value ?? string.Empty;
        Match match = PathRegex.Match(path);

        if (!match.Success)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status404NotFound, "Unknown SSZ endpoint");
            return;
        }

        if (!int.TryParse(match.Groups["version"].Value, out int version))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status404NotFound, "Invalid version in path");
            return;
        }
        string resource = match.Groups["resource"].Value.ToLowerInvariant();
        string extra = match.Groups["extra"].Value;

        if (_logger.IsTrace)
            _logger.Trace($"SSZ-REST {ctx.Request.Method} /engine/v{version}/{resource}" +
                          (extra.Length > 0 ? "/" + extra : ""));

        if (!TryResolveHandler(ctx.Request.Method, resource, version, out ISszEndpointHandler? handler))
        {
            await WriteErrorAsync(ctx, StatusCodes.Status404NotFound,
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
            await WriteErrorAsync(ctx, StatusCodes.Status413PayloadTooLarge, ex.Message);
            return;
        }

        try
        {
            await handler!.HandleAsync(ctx, version, extra, body);
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"SSZ-REST handler error for {path}", ex);
            await WriteErrorAsync(ctx, StatusCodes.Status500InternalServerError, "Internal server error");
        }
    }

    private bool TryResolveHandler(string method, string resource, int version, out ISszEndpointHandler? handler)
    {
        IEnumerable<ISszEndpointHandler> candidates = _routes[(method, resource.ToLowerInvariant())];
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
            return ctx.Request.ContentType?.Contains(OctetStream, StringComparison.OrdinalIgnoreCase) == true;

        if (ctx.Request.Method == "GET")
        {
            foreach (string? v in ctx.Request.Headers.Accept)
                if (v is not null && v.Contains(OctetStream, StringComparison.OrdinalIgnoreCase))
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

    private static async Task WriteErrorAsync(HttpContext ctx, int status, string message)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain";
        await ctx.Response.WriteAsync(message);
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

file static class TupleComparerExtensions
{
    internal static IEqualityComparer<(string, string)> ToTupleComparer(
        this StringComparer inner) => new TupleStringComparer(inner);

    private sealed class TupleStringComparer(StringComparer inner)
        : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y)
            => inner.Equals(x.Item1, y.Item1) && inner.Equals(x.Item2, y.Item2);

        public int GetHashCode((string, string) obj)
            => HashCode.Combine(inner.GetHashCode(obj.Item1), inner.GetHashCode(obj.Item2));
    }
}
