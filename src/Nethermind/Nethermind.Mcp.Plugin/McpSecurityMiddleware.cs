// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Nethermind.Mcp.Plugin;

/// <summary>Outcome of <see cref="McpSecurityMiddleware.Evaluate"/>.</summary>
internal enum McpRequestVerdict
{
    Allowed,
    BadHost,
    BadOrigin,
    Unauthorized,
}

/// <summary>
/// Guards every request to the MCP listener: <c>Host</c> check (DNS-rebinding defence), then <c>Origin</c> check
/// (cross-site browser requests), then optional bearer authentication.
/// </summary>
/// <remarks>
/// The decision logic is in <see cref="Evaluate"/>, which is free of I/O so it can be tested directly. No CORS headers are
/// ever emitted, so browsers cannot read responses from origins that are not same-origin with the listener.
/// </remarks>
internal sealed class McpSecurityMiddleware
{
    private const string BearerPrefix = "Bearer ";
    private const int DefaultHttpPort = 80;

    private readonly string[] _allowedOrigins;
    private readonly byte[]? _tokenHash;

    /// <param name="allowedOrigins">Allowed origins, already normalized to <c>scheme://host[:port]</c>.</param>
    /// <param name="authToken">The required bearer token, or <see langword="null"/> when authentication is disabled.</param>
    public McpSecurityMiddleware(string[] allowedOrigins, string? authToken)
    {
        ArgumentNullException.ThrowIfNull(allowedOrigins);
        _allowedOrigins = allowedOrigins;
        _tokenHash = authToken is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(authToken));
    }

    /// <summary>Gets whether requests must carry a bearer token.</summary>
    public bool RequiresAuth => _tokenHash is not null;

    /// <summary>Decides whether a request may proceed.</summary>
    /// <param name="host">The <c>Host</c> header value(s).</param>
    /// <param name="localPort">The local port the connection arrived on.</param>
    /// <param name="origin">The <c>Origin</c> header value(s); empty when absent.</param>
    /// <param name="authorization">The <c>Authorization</c> header value(s); empty when absent.</param>
    public McpRequestVerdict Evaluate(StringValues host, int localPort, StringValues origin, StringValues authorization)
    {
        if (host.Count != 1 || !IsAllowedHost(host[0], localPort)) return McpRequestVerdict.BadHost;
        if (!IsAllowedOrigin(origin)) return McpRequestVerdict.BadOrigin;
        if (!IsAuthorized(authorization)) return McpRequestVerdict.Unauthorized;
        return McpRequestVerdict.Allowed;
    }

    /// <summary>Returns whether <paramref name="host"/> names the loopback listener on <paramref name="localPort"/>.</summary>
    /// <remarks>Accepted: <c>127.0.0.1</c>, <c>localhost</c> and <c>[::1]</c>, each with the port, case-insensitive.
    /// The port may be omitted only when the listener is on port 80, as HTTP clients omit the default port.</remarks>
    internal static bool IsAllowedHost(string? host, int localPort)
    {
        if (string.IsNullOrEmpty(host)) return false;

        ReadOnlySpan<char> value = host;
        ReadOnlySpan<char> name = value;
        int colon = value.LastIndexOf(':');
        int bracket = value.LastIndexOf(']');
        if (colon > bracket)
        {
            name = value[..colon];
            ReadOnlySpan<char> port = value[(colon + 1)..];
            if (port.Length == 0
                || !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPort)
                || parsedPort != localPort)
            {
                return false;
            }
        }
        else if (localPort != DefaultHttpPort)
        {
            return false;
        }

        return name.Equals("127.0.0.1", StringComparison.Ordinal)
            || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("[::1]", StringComparison.Ordinal);
    }

    /// <summary>Returns whether the request carries no <c>Origin</c> header or exactly one allowed origin.</summary>
    internal bool IsAllowedOrigin(StringValues origin)
    {
        if (origin.Count == 0) return true;
        if (origin.Count != 1) return false;

        string? value = origin[0];
        if (string.IsNullOrEmpty(value)) return false;

        foreach (string allowed in _allowedOrigins)
        {
            if (string.Equals(allowed, value, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>Returns whether the request satisfies authentication; always <see langword="true"/> when it is disabled.</summary>
    /// <remarks>Tokens are compared as SHA-256 digests in constant time, so neither content nor length leaks through timing.</remarks>
    internal bool IsAuthorized(StringValues authorization)
    {
        if (_tokenHash is null) return true;
        if (authorization.Count != 1) return false;

        string? value = authorization[0];
        if (value is null || value.Length <= BearerPrefix.Length
            || !value.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Span<byte> presentedHash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(value, BearerPrefix.Length, value.Length - BearerPrefix.Length), presentedHash);
        return CryptographicOperations.FixedTimeEquals(presentedHash, _tokenHash);
    }

    /// <summary>Runs the guard for <paramref name="context"/>, short-circuiting rejected requests.</summary>
    public Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        HttpRequest request = context.Request;
        McpRequestVerdict verdict = Evaluate(
            request.Headers.Host,
            context.Connection.LocalPort,
            request.Headers.Origin,
            request.Headers.Authorization);

        switch (verdict)
        {
            case McpRequestVerdict.Allowed:
                return next(context);
            case McpRequestVerdict.BadHost:
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                break;
            case McpRequestVerdict.BadOrigin:
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                break;
            default:
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                break;
        }

        return Task.CompletedTask;
    }
}
