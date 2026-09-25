// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Nethermind.Mcp.Plugin;

/// <summary>Outcome of <see cref="McpSecurityMiddleware.Evaluate(StringValues, int, StringValues, StringValues, IPAddress?)"/>.</summary>
internal enum McpRequestVerdict
{
    Allowed,
    BadHost,
    BadOrigin,
    Unauthorized,
    TooManyAttempts,
}

/// <summary>
/// Guards every request to the MCP listener: <c>Host</c> check (DNS-rebinding defence), then <c>Origin</c> check
/// (cross-site browser requests), then optional bearer authentication with a per-client failure limit.
/// </summary>
/// <remarks>
/// The decision logic is in <see cref="Evaluate(StringValues, int, StringValues, StringValues, IPAddress?)"/>, which is free of I/O
/// so it can be tested directly. No CORS headers are ever emitted, so browsers cannot read responses from origins that are not
/// same-origin with the listener.
/// </remarks>
internal sealed class McpSecurityMiddleware
{
    private const string BearerPrefix = "Bearer ";

    private readonly string[] _allowedOrigins;
    private readonly byte[]? _tokenHash;
    private readonly McpHostPolicy _hostPolicy;
    private readonly McpAuthFailureLimiter? _limiter;

    /// <param name="allowedOrigins">Allowed origins, already normalized to <c>scheme://host[:port]</c>.</param>
    /// <param name="authToken">The required bearer token, or <see langword="null"/> when authentication is disabled.</param>
    /// <param name="hostPolicy">The accepted <c>Host</c> header values; defaults to the loopback names over plain HTTP.</param>
    /// <param name="limiter">Limits failed authentications per client address; <see langword="null"/> disables the limit.</param>
    public McpSecurityMiddleware(string[] allowedOrigins, string? authToken, McpHostPolicy? hostPolicy = null, McpAuthFailureLimiter? limiter = null)
    {
        ArgumentNullException.ThrowIfNull(allowedOrigins);
        _allowedOrigins = allowedOrigins;
        _tokenHash = authToken is null ? null : SHA256.HashData(Encoding.UTF8.GetBytes(authToken));
        _hostPolicy = hostPolicy ?? McpHostPolicy.Loopback(McpHostPolicy.DefaultHttpPort, []);
        _limiter = limiter;
    }

    /// <summary>Gets whether requests must carry a bearer token.</summary>
    public bool RequiresAuth => _tokenHash is not null;

    /// <summary>Decides whether a request from an unknown client may proceed; no failure limit applies.</summary>
    public McpRequestVerdict Evaluate(StringValues host, int localPort, StringValues origin, StringValues authorization) =>
        Evaluate(host, localPort, origin, authorization, null);

    /// <summary>Decides whether a request may proceed.</summary>
    /// <param name="host">The <c>Host</c> header value(s).</param>
    /// <param name="localPort">The local port the connection arrived on.</param>
    /// <param name="origin">The <c>Origin</c> header value(s); empty when absent.</param>
    /// <param name="authorization">The <c>Authorization</c> header value(s); empty when absent.</param>
    /// <param name="remoteAddress">The client address, used by the failed-authentication limit.</param>
    /// <remarks>A failed authentication is recorded against <paramref name="remoteAddress"/>; once the limit is reached the
    /// client gets <see cref="McpRequestVerdict.TooManyAttempts"/> without its token being checked, until the window passes.</remarks>
    public McpRequestVerdict Evaluate(StringValues host, int localPort, StringValues origin, StringValues authorization, IPAddress? remoteAddress)
    {
        if (host.Count != 1 || !_hostPolicy.IsAllowed(host[0], localPort)) return McpRequestVerdict.BadHost;
        if (!IsAllowedOrigin(origin)) return McpRequestVerdict.BadOrigin;
        if (_tokenHash is null) return McpRequestVerdict.Allowed;

        McpAuthFailureLimiter? limiter = remoteAddress is null ? null : _limiter;
        if (limiter?.IsBlocked(remoteAddress!) == true) return McpRequestVerdict.TooManyAttempts;
        if (IsAuthorized(authorization)) return McpRequestVerdict.Allowed;

        limiter?.RecordFailure(remoteAddress!);
        return McpRequestVerdict.Unauthorized;
    }

    /// <summary>Returns whether <paramref name="host"/> names the loopback listener on <paramref name="localPort"/> over plain HTTP.</summary>
    /// <remarks>Accepted: <c>127.0.0.1</c>, <c>localhost</c> and <c>[::1]</c>, each with the port, case-insensitive.
    /// The port may be omitted only when the listener is on port 80, as HTTP clients omit the default port.</remarks>
    internal static bool IsAllowedHost(string? host, int localPort) => McpHostPolicy.IsLoopbackName(host, localPort, McpHostPolicy.DefaultHttpPort);

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
            request.Headers.Authorization,
            context.Connection.RemoteIpAddress);

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
            case McpRequestVerdict.TooManyAttempts:
                context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                context.Response.Headers.RetryAfter = ((int)Math.Ceiling(_limiter!.Window.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                break;
            default:
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.Headers.WWWAuthenticate = "Bearer";
                break;
        }

        return Task.CompletedTask;
    }
}

/// <summary>An accepted <c>Host</c> header value: a normalized host name or IP literal, and an optional port.</summary>
/// <param name="Name">The lower-case host name, IPv4 literal, or bracketed canonical IPv6 literal.</param>
/// <param name="Port">The required port, or <see langword="null"/> to accept any port.</param>
internal sealed record McpAllowedHost(string Name, int? Port)
{
    /// <summary>Parses <c>host</c>, <c>host:port</c>, <c>[ipv6]</c> or <c>[ipv6]:port</c>; rejects schemes, paths, user info and wildcards.</summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out McpAllowedHost? host)
    {
        host = null;
        if (string.IsNullOrEmpty(value)) return false;

        ReadOnlySpan<char> span = value;
        ReadOnlySpan<char> name;
        ReadOnlySpan<char> portText = default;
        bool hasPort;
        string normalized;

        if (span[0] == '[')
        {
            int close = span.IndexOf(']');
            if (close < 0 || !IPAddress.TryParse(span[1..close], out IPAddress? ipv6) || ipv6.AddressFamily != AddressFamily.InterNetworkV6)
                return false;

            ReadOnlySpan<char> rest = span[(close + 1)..];
            hasPort = rest.Length > 0;
            if (hasPort)
            {
                if (rest[0] != ':') return false;
                portText = rest[1..];
            }

            normalized = $"[{ipv6}]";
        }
        else
        {
            int colon = span.IndexOf(':');
            hasPort = colon >= 0;
            name = hasPort ? span[..colon] : span;
            if (hasPort) portText = span[(colon + 1)..];

            string nameText = name.ToString();
            if (Uri.CheckHostName(nameText) is not (UriHostNameType.Dns or UriHostNameType.IPv4)) return false;
            normalized = nameText.ToLowerInvariant();
        }

        int? port = null;
        if (hasPort)
        {
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPort)
                || parsedPort is < 1 or > IPEndPoint.MaxPort)
            {
                return false;
            }

            port = parsedPort;
        }

        host = new McpAllowedHost(normalized, port);
        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => Port is null ? Name : $"{Name}:{Port}";
}

/// <summary>Decides which <c>Host</c> header values the listener accepts.</summary>
/// <remarks>
/// On loopback the loopback names (<c>127.0.0.1</c>, <c>localhost</c>, <c>[::1]</c>) with the listener port are accepted, plus
/// <see cref="IMcpConfig.AllowedHosts"/>. In remote mode only <see cref="IMcpConfig.AllowedHosts"/> and the bound IP literal are.
/// </remarks>
internal sealed class McpHostPolicy
{
    internal const int DefaultHttpPort = 80;
    internal const int DefaultHttpsPort = 443;

    private readonly bool _allowLoopbackNames;
    private readonly McpAllowedHost[] _allowedHosts;
    private readonly int _defaultPort;

    private McpHostPolicy(bool allowLoopbackNames, McpAllowedHost[] allowedHosts, int defaultPort)
    {
        _allowLoopbackNames = allowLoopbackNames;
        _allowedHosts = allowedHosts;
        _defaultPort = defaultPort;
    }

    /// <summary>Creates the loopback policy.</summary>
    /// <param name="defaultPort">The scheme's default port (80 or 443), which clients may omit.</param>
    /// <param name="allowedHosts">Extra accepted hosts.</param>
    public static McpHostPolicy Loopback(int defaultPort, McpAllowedHost[] allowedHosts) => new(true, allowedHosts, defaultPort);

    /// <summary>Creates the remote-mode policy: only <paramref name="allowedHosts"/> and, unless it is a wildcard, <paramref name="boundAddress"/>.</summary>
    public static McpHostPolicy Remote(IPAddress boundAddress, McpAllowedHost[] allowedHosts)
    {
        if (boundAddress.Equals(IPAddress.Any) || boundAddress.Equals(IPAddress.IPv6Any)) return new(false, allowedHosts, DefaultHttpsPort);

        string literal = boundAddress.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{boundAddress}]" : boundAddress.ToString();
        return new(false, [.. allowedHosts, new McpAllowedHost(literal, null)], DefaultHttpsPort);
    }

    /// <summary>Returns whether <paramref name="host"/> is accepted on a connection to <paramref name="localPort"/>.</summary>
    public bool IsAllowed(string? host, int localPort)
    {
        if (_allowLoopbackNames && IsLoopbackName(host, localPort, _defaultPort)) return true;
        if (_allowedHosts.Length == 0 || !McpAllowedHost.TryParse(host, out McpAllowedHost? presented)) return false;

        int presentedPort = presented.Port ?? _defaultPort;
        foreach (McpAllowedHost allowed in _allowedHosts)
        {
            if (string.Equals(allowed.Name, presented.Name, StringComparison.Ordinal) && (allowed.Port is null || allowed.Port == presentedPort))
                return true;
        }

        return false;
    }

    /// <summary>Returns whether <paramref name="host"/> is a loopback name with <paramref name="localPort"/>, which may be omitted when it is <paramref name="defaultPort"/>.</summary>
    internal static bool IsLoopbackName(string? host, int localPort, int defaultPort)
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
        else if (localPort != defaultPort)
        {
            return false;
        }

        return name.Equals("127.0.0.1", StringComparison.Ordinal)
            || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || name.Equals("[::1]", StringComparison.Ordinal);
    }
}

/// <summary>Counts failed bearer authentications per client and blocks a client that fails too often.</summary>
/// <remarks>
/// Sliding window: a client with <see cref="MaxFailures"/> failures within <see cref="Window"/> is blocked until the oldest of
/// them leaves the window. IPv4-mapped IPv6 addresses count as IPv4, and IPv6 clients are grouped by /64, the smallest block
/// usually assigned to one host. At most <see cref="MaxTrackedClients"/> clients are tracked; when the table is full of active
/// entries, further new clients share one bucket, so a distributed attack degrades to a shared limit rather than unbounded memory.
/// Only used in remote mode: on loopback every client shares one address, so a local process could lock out the legitimate agent.
/// Thread-safe.
/// </remarks>
internal sealed class McpAuthFailureLimiter(TimeProvider? timeProvider = null)
{
    /// <summary>The failures within <see cref="Window"/> that block a client.</summary>
    public const int MaxFailures = 10;

    /// <summary>The maximum number of clients tracked at once.</summary>
    public const int MaxTrackedClients = 4096;

    private static readonly IPAddress OverflowKey = IPAddress.None;

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<IPAddress, Queue<long>> _failures = [];
    private readonly Lock _lock = new();

    /// <summary>Gets the sliding window failures are counted in.</summary>
    public TimeSpan Window { get; } = TimeSpan.FromSeconds(60);

    /// <summary>Gets the number of tracked clients.</summary>
    internal int TrackedClients
    {
        get
        {
            lock (_lock) return _failures.Count;
        }
    }

    /// <summary>Returns whether <paramref name="address"/> has reached the failure limit.</summary>
    public bool IsBlocked(IPAddress address)
    {
        long now = _time.GetUtcNow().UtcTicks;
        IPAddress key = Normalize(address);
        lock (_lock)
        {
            if (!_failures.TryGetValue(key, out Queue<long>? failures) && !(IsFull() && _failures.TryGetValue(OverflowKey, out failures)))
                return false;

            Expire(failures, now);
            return failures.Count >= MaxFailures;
        }
    }

    /// <summary>Records a failed authentication of <paramref name="address"/>.</summary>
    public void RecordFailure(IPAddress address)
    {
        long now = _time.GetUtcNow().UtcTicks;
        IPAddress key = Normalize(address);
        lock (_lock)
        {
            if (!_failures.TryGetValue(key, out Queue<long>? failures))
            {
                if (IsFull()) Purge(now);
                if (IsFull()) key = OverflowKey;
                if (!_failures.TryGetValue(key, out failures))
                {
                    failures = new Queue<long>(MaxFailures);
                    _failures[key] = failures;
                }
            }

            Expire(failures, now);
            if (failures.Count == MaxFailures) failures.Dequeue();
            failures.Enqueue(now);
        }
    }

    private bool IsFull() => _failures.Count >= MaxTrackedClients;

    private void Expire(Queue<long> failures, long now)
    {
        long cutoff = now - Window.Ticks;
        while (failures.Count > 0 && failures.Peek() <= cutoff) failures.Dequeue();
    }

    private void Purge(long now)
    {
        List<IPAddress>? idle = null;
        foreach ((IPAddress key, Queue<long> failures) in _failures)
        {
            Expire(failures, now);
            if (failures.Count == 0) (idle ??= []).Add(key);
        }

        if (idle is null) return;
        foreach (IPAddress key in idle) _failures.Remove(key);
    }

    private static IPAddress Normalize(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address;

        Span<byte> bytes = stackalloc byte[16];
        address.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return new IPAddress(bytes);
    }
}
