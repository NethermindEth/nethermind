// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Nethermind.JsonRpc;
using Nethermind.Logging;

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
    private const string RejectionMessage = "invalid host specified";

    public static void UseAllowedHosts(this IApplicationBuilder app, IJsonRpcConfig config, IJsonRpcUrlCollection urls, ILogger logger)
    {
        StringSegment[]? allowedHosts = ToPatterns(config.AllowedHosts, nameof(IJsonRpcConfig.AllowedHosts), logger);
        StringSegment[]? engineAllowedHosts = ToPatterns(config.EngineAllowedHosts, nameof(IJsonRpcConfig.EngineAllowedHosts), logger);
        if (allowedHosts is null && engineAllowedHosts is null) return;

        if (allowedHosts is not null && Array.IndexOf(config.CorsOrigins, AnyHost) >= 0 && logger.IsWarn)
            logger.Warn("JsonRpc.AllowedHosts is set but JsonRpc.CorsOrigins is '*', so web pages can still call the JSON-RPC server. Set JsonRpc.CorsOrigins too.");

        app.Use((ctx, next) =>
        {
            StringSegment[]? patterns = urls.TryGetValue(ctx.Connection.LocalPort, out JsonRpcUrl? url) && url.IsAuthenticated
                ? engineAllowedHosts
                : allowedHosts;

            // The raw header, because HttpRequest.Host decodes punycode and throws on an invalid label.
            if (IsAllowed(new HostString(ctx.Request.Headers.Host.ToString()), patterns)) return next(ctx);

            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return ctx.Response.WriteAsync(RejectionMessage);
        });
    }

    /// <param name="patterns">The allowed host patterns, or <see langword="null"/> to allow all hosts.</param>
    internal static bool IsAllowed(HostString host, StringSegment[]? patterns) =>
        patterns is null ||
        !host.HasValue ||
        HostString.MatchesAny(host.Value, patterns) ||
        IPAddress.TryParse(host.Host, out _);

    private static StringSegment[]? ToPatterns(string[]? hosts, string settingName, ILogger logger)
    {
        if (hosts is null || Array.IndexOf(hosts, AnyHost) >= 0) return null;

        StringSegment[] patterns = new StringSegment[hosts.Length];
        for (int i = 0; i < hosts.Length; i++)
        {
            string host = hosts[i];
            string? pattern = ToAscii(host);
            if ((pattern is null || NeverMatches(pattern)) && logger.IsWarn)
                logger.Warn($"JsonRpc.{settingName} entry '{host}' never matches. Use a host name without a port or scheme, optionally with a leading '*.'. IP addresses are always accepted.");

            patterns[i] = new StringSegment(pattern ?? host);
        }

        return patterns;
    }

    /// <summary>Converts an internationalized host name to punycode, the form that clients send in the <c>Host</c> header.</summary>
    private static string? ToAscii(string host)
    {
        try
        {
            return new HostString(host).ToUriComponent();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool NeverMatches(string pattern) =>
        pattern.Length == 0 ||
        pattern.AsSpan().IndexOfAny(':', '/') >= 0 ||
        pattern.IndexOf('*', pattern.StartsWith("*.", StringComparison.Ordinal) ? 1 : 0) >= 0;
}
