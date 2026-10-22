// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nethermind.JsonRpc;

namespace Nethermind.Runner.JsonRpc;

/// <summary>
/// Rejects requests whose <c>Host</c> header is not in <see cref="IJsonRpcConfig.AllowedHosts"/>,
/// or in <see cref="IJsonRpcConfig.EngineAllowedHosts"/> on authenticated URLs, with HTTP 403.
/// </summary>
/// <remarks>
/// Protects against DNS rebinding, like Geth's <c>--http.vhosts</c> and <c>--authrpc.vhosts</c>.
/// As in Geth, IP-literal hosts and a missing <c>Host</c> header are always accepted, because DNS rebinding needs a host name.
/// </remarks>
internal static class AllowedHostsFilter
{
    private const string AnyHost = "*";

    public static void UseAllowedHosts(this IApplicationBuilder app, IJsonRpcConfig config, IJsonRpcUrlCollection urls)
    {
        StringSegment[]? allowedHosts = ToPatterns(config.AllowedHosts);
        StringSegment[]? engineAllowedHosts = ToPatterns(config.EngineAllowedHosts);
        if (allowedHosts is null && engineAllowedHosts is null) return;

        app.Use((ctx, next) =>
        {
            StringSegment[]? patterns = urls.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl? url) && url.IsAuthenticated
                ? engineAllowedHosts
                : allowedHosts;

            if (IsAllowed(ctx.Request.Host, patterns)) return next(ctx);

            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        });
    }

    /// <param name="patterns">The allowed host patterns, or <see langword="null"/> to allow all hosts.</param>
    internal static bool IsAllowed(HostString host, StringSegment[]? patterns) =>
        patterns is null ||
        !host.HasValue ||
        IPAddress.TryParse(host.Host, out _) ||
        HostString.MatchesAny(host.Value, patterns);

    private static StringSegment[]? ToPatterns(string[] hosts) =>
        Array.IndexOf(hosts, AnyHost) >= 0 ? null : Array.ConvertAll(hosts, static h => new StringSegment(h));
}
