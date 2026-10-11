// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.JsonRpc;
using Nethermind.Monitoring.Config;

namespace Nethermind.Mcp.Plugin;

/// <summary>The validated listener settings of the MCP server, with the TLS certificate and bearer token loaded.</summary>
internal sealed class McpListenerSettings : IDisposable
{
    /// <summary>Gets the address the listener binds to.</summary>
    public required IPAddress Address { get; init; }

    /// <summary>Gets whether <see cref="Address"/> is not a loopback address, which puts the server in remote mode.</summary>
    public bool IsRemote => !IPAddress.IsLoopback(Address);

    /// <summary>Gets the allowed browser origins, normalized to <c>scheme://host[:port]</c>.</summary>
    public required string[] AllowedOrigins { get; init; }

    /// <summary>Gets the configured extra <c>Host</c> header values.</summary>
    public required McpAllowedHost[] AllowedHosts { get; init; }

    /// <summary>Gets the bearer token, or <see langword="null"/> when authentication is disabled.</summary>
    public string? AuthToken { get; init; }

    /// <summary>Gets the server certificate with its private key, or <see langword="null"/> when serving plain HTTP.</summary>
    public X509Certificate2? Certificate { get; init; }

    /// <summary>Gets the intermediate certificates sent after <see cref="Certificate"/>.</summary>
    public X509Certificate2Collection CertificateChain { get; init; } = [];

    /// <summary>Gets non-fatal problems to log, such as a certificate close to expiry.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <inheritdoc/>
    public void Dispose()
    {
        Certificate?.Dispose();
        foreach (X509Certificate2 certificate in CertificateChain) certificate.Dispose();
    }
}

/// <summary>Validates <see cref="IMcpConfig"/> against itself and the other node listeners before the MCP server starts.</summary>
internal static class McpConfigValidator
{
    internal const int MinAuthTokenLength = 32;
    internal static readonly TimeSpan CertificateExpiryWarning = TimeSpan.FromDays(14);

    /// <summary>Throws when the MCP configuration is invalid or conflicts with another node listener.</summary>
    /// <param name="config">The MCP configuration.</param>
    /// <param name="rpc">The JSON-RPC configuration, used for port collisions and the timeout and gas caps.</param>
    /// <param name="metrics">The metrics configuration, used for the Prometheus exposure port collision.</param>
    /// <exception cref="InvalidConfigurationException">The configuration is invalid.</exception>
    public static void Validate(IMcpConfig config, IJsonRpcConfig rpc, IMetricsConfig? metrics = null)
    {
        using McpListenerSettings settings = Load(config, rpc, metrics);
    }

    /// <summary>Validates the configuration and loads the files it names (token, certificate and key).</summary>
    /// <param name="config">The MCP configuration.</param>
    /// <param name="rpc">The JSON-RPC configuration, used for port collisions and the timeout and gas caps.</param>
    /// <param name="metrics">The metrics configuration, used for the Prometheus exposure port collision.</param>
    /// <param name="now">The current time for certificate expiry checks; defaults to the system clock.</param>
    /// <returns>The settings; the caller owns and disposes them.</returns>
    /// <exception cref="InvalidConfigurationException">The configuration is invalid or a file cannot be loaded.</exception>
    /// <remarks>
    /// A loopback <see cref="IMcpConfig.Host"/> serves plain HTTP, or HTTPS when a certificate is configured, with an optional token.
    /// Any other address is remote mode, which requires HTTPS, a token and at least one <see cref="IMcpConfig.AllowedHosts"/> entry.
    /// </remarks>
    public static McpListenerSettings Load(IMcpConfig config, IJsonRpcConfig rpc, IMetricsConfig? metrics = null, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rpc);

        IPAddress address = ParseAddress(config.Host);

        if (config.Port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort)
            throw Forbidden($"Mcp.Port must be between {IPEndPoint.MinPort} and {IPEndPoint.MaxPort}, got {config.Port}.");

        ValidatePortCollisions(config.Port, rpc, metrics);

        RequirePositive(config.MaxRequestBodySize, nameof(IMcpConfig.MaxRequestBodySize));
        RequirePositive(config.MaxConcurrentToolCalls, nameof(IMcpConfig.MaxConcurrentToolCalls));
        RequirePositive(config.ToolTimeout, nameof(IMcpConfig.ToolTimeout));
        RequirePositive(config.MaxResultSize, nameof(IMcpConfig.MaxResultSize));
        RequirePositive(config.MaxLogBlockRange, nameof(IMcpConfig.MaxLogBlockRange));
        RequirePositive(config.MaxLogs, nameof(IMcpConfig.MaxLogs));
        RequirePositive(config.MaxCallGas, nameof(IMcpConfig.MaxCallGas));
        RequirePositive(config.MaxCallDataSize, nameof(IMcpConfig.MaxCallDataSize));
        RequirePositive(config.MaxIndexedLogBlockRange, nameof(IMcpConfig.MaxIndexedLogBlockRange));
        RequirePositive(config.MaxTraceCalls, nameof(IMcpConfig.MaxTraceCalls));

        if (config.ToolTimeout > rpc.Timeout)
            throw Conflicting($"Mcp.ToolTimeout ({config.ToolTimeout} ms) must not exceed JsonRpc.Timeout ({rpc.Timeout} ms).");

        if (rpc.GasCap.IsGasCapped() && (ulong)config.MaxCallGas > rpc.GasCap!.Value)
            throw Conflicting($"Mcp.MaxCallGas ({config.MaxCallGas}) must not exceed JsonRpc.GasCap ({rpc.GasCap.Value}).");

        string[] origins = NormalizeOrigins(config.AllowedOrigins);
        McpAllowedHost[] allowedHosts = ParseAllowedHosts(config.AllowedHosts);

        bool hasCertificate = !string.IsNullOrWhiteSpace(config.TlsCertificatePath);
        bool hasKey = !string.IsNullOrWhiteSpace(config.TlsCertificateKeyPath);
        if (hasCertificate != hasKey)
            throw Forbidden("Mcp.TlsCertificatePath and Mcp.TlsCertificateKeyPath must be set together: HTTPS needs both the PEM certificate and its PEM private key.");

        if (!IPAddress.IsLoopback(address))
        {
            string remote = $"Mcp.Host {address} is not a loopback address, which enables remote mode.";
            if (!hasCertificate)
                throw Forbidden($"{remote} Remote mode requires HTTPS: set Mcp.TlsCertificatePath and Mcp.TlsCertificateKeyPath. Plain HTTP is never served on a non-loopback address; use 127.0.0.1 for local-only access.");
            if (string.IsNullOrWhiteSpace(config.AuthTokenFile))
                throw Forbidden($"{remote} Remote mode requires bearer authentication: set Mcp.AuthTokenFile to a file holding a random token of at least {MinAuthTokenLength} characters.");
            if (allowedHosts.Length == 0)
                throw Forbidden($"{remote} Remote mode requires Mcp.AllowedHosts: the host names (or IP addresses) clients use to reach this node, such as node.example.com.");
        }

        string? token = LoadAuthToken(config.AuthTokenFile);

        if (!hasCertificate)
        {
            return new McpListenerSettings { Address = address, AllowedOrigins = origins, AllowedHosts = allowedHosts, AuthToken = token };
        }

        List<string> warnings = [];
        X509Certificate2 certificate = LoadCertificate(
            config.TlsCertificatePath!, config.TlsCertificateKeyPath!, now ?? DateTimeOffset.UtcNow, warnings, out X509Certificate2Collection chain);

        return new McpListenerSettings
        {
            Address = address,
            AllowedOrigins = origins,
            AllowedHosts = allowedHosts,
            AuthToken = token,
            Certificate = certificate,
            CertificateChain = chain,
            Warnings = warnings,
        };
    }

    /// <summary>Parses <paramref name="host"/> as an IP address literal.</summary>
    /// <exception cref="InvalidConfigurationException">The value is not an IP address literal.</exception>
    internal static IPAddress ParseAddress(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || !IPAddress.TryParse(host, out IPAddress? address) || host.Trim() != host)
            throw Forbidden($"Mcp.Host must be an IP address literal such as 127.0.0.1, ::1 or (remote mode) 0.0.0.0; host names are not supported, got '{host}'.");

        return address;
    }

    /// <summary>Validates allowed origins and returns them as <c>scheme://host[:port]</c>, the form browsers send in <c>Origin</c>.</summary>
    /// <exception cref="InvalidConfigurationException">An entry is not an http(s) origin.</exception>
    internal static string[] NormalizeOrigins(string[]? origins)
    {
        if (origins is null || origins.Length == 0) return [];

        string[] normalized = new string[origins.Length];
        for (int i = 0; i < origins.Length; i++)
        {
            string? origin = origins[i];
            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || uri.UserInfo.Length != 0
                || uri.PathAndQuery != "/"
                || uri.Fragment.Length != 0
                || origin!.TrimEnd('/').EndsWith('?')
                || origin.Contains('#'))
            {
                throw Forbidden($"Mcp.AllowedOrigins entry '{origin}' must be an origin of the form http(s)://host[:port].");
            }

            normalized[i] = uri.GetLeftPart(UriPartial.Authority);
        }

        return normalized;
    }

    /// <summary>Validates <see cref="IMcpConfig.AllowedHosts"/> entries (host or host:port, IPv6 in brackets).</summary>
    /// <exception cref="InvalidConfigurationException">An entry is not a bare host name, IP literal or either with a port.</exception>
    internal static McpAllowedHost[] ParseAllowedHosts(string[]? hosts)
    {
        if (hosts is null || hosts.Length == 0) return [];

        McpAllowedHost[] parsed = new McpAllowedHost[hosts.Length];
        for (int i = 0; i < hosts.Length; i++)
        {
            if (!McpAllowedHost.TryParse(hosts[i], out McpAllowedHost? host))
            {
                throw Forbidden($"Mcp.AllowedHosts entry '{hosts[i]}' must be a host name or IP address with an optional port, such as node.example.com, " +
                    "node.example.com:8555, 203.0.113.7 or [2001:db8::1]:8555, without scheme, path or wildcards.");
            }

            parsed[i] = host;
        }

        return parsed;
    }

    /// <summary>Reads the bearer token from <paramref name="path"/>, or returns <see langword="null"/> when no file is configured.</summary>
    /// <exception cref="InvalidConfigurationException">The file cannot be read, the token is shorter than <see cref="MinAuthTokenLength"/>, or it holds a character other than visible ASCII.</exception>
    internal static string? LoadAuthToken(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string token;
        try
        {
            token = File.ReadAllText(path).Trim();
        }
        catch (Exception e) when (IsFileError(e))
        {
            throw Forbidden($"Mcp.AuthTokenFile '{path}' cannot be read: {e.Message}");
        }

        if (token.Length < MinAuthTokenLength)
            throw Forbidden($"Mcp.AuthTokenFile '{path}' must contain a token of at least {MinAuthTokenLength} characters.");

        // Clients send the token in an Authorization header, which cannot carry control characters, spaces or non-ASCII text
        // (RFC 9110 field values; RFC 6750 b64token is a subset of visible ASCII). The message names the position, never the token.
        int invalid = token.AsSpan().IndexOfAnyExceptInRange('\x21', '\x7e');
        if (invalid >= 0)
            throw Forbidden($"Mcp.AuthTokenFile '{path}' holds a token with a character that is not visible ASCII (U+{(int)token[invalid]:X4} at position {invalid + 1}), " +
                "which no client can send in an Authorization header. Put the token on a single line using only visible ASCII, e.g. `openssl rand -hex 32`.");

        return token;
    }

    /// <summary>Loads a PEM certificate (plus any chain certificates in the same file) and its PEM private key.</summary>
    /// <param name="certificatePath">The PEM certificate file.</param>
    /// <param name="keyPath">The PEM private key file.</param>
    /// <param name="now">The current time, for the expiry warnings.</param>
    /// <param name="warnings">Receives expiry warnings.</param>
    /// <param name="chain">Receives the certificates following the first one in <paramref name="certificatePath"/>.</param>
    /// <exception cref="InvalidConfigurationException">A file cannot be read, holds no usable PEM, or the key does not match the certificate.</exception>
    internal static X509Certificate2 LoadCertificate(string certificatePath, string keyPath, DateTimeOffset now, List<string> warnings, out X509Certificate2Collection chain)
    {
        string certificatePem = ReadPem(certificatePath, nameof(IMcpConfig.TlsCertificatePath));
        string keyPem = ReadPem(keyPath, nameof(IMcpConfig.TlsCertificateKeyPath));

        X509Certificate2 certificate;
        try
        {
            certificate = X509Certificate2.CreateFromPem(certificatePem, keyPem);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        {
            throw Forbidden($"Mcp.TlsCertificatePath '{certificatePath}' and Mcp.TlsCertificateKeyPath '{keyPath}' must hold a PEM certificate and its matching " +
                $"unencrypted PEM private key: {e.Message}");
        }

        if (OperatingSystem.IsWindows())
        {
            // SChannel cannot use the ephemeral key of a PEM-loaded certificate; a PKCS#12 round trip persists it.
            X509Certificate2 usable = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
            certificate.Dispose();
            certificate = usable;
        }

        chain = [];
        X509Certificate2Collection all = [];
        all.ImportFromPem(certificatePem);
        for (int i = 1; i < all.Count; i++) chain.Add(all[i]);
        if (all.Count > 0) all[0].Dispose();

        DateTimeOffset notAfter = new(certificate.NotAfter.ToUniversalTime());
        DateTimeOffset notBefore = new(certificate.NotBefore.ToUniversalTime());
        if (notAfter <= now)
            warnings.Add($"The MCP TLS certificate '{certificatePath}' expired on {notAfter:u}; clients will reject it until it is renewed.");
        else if (notAfter - now < CertificateExpiryWarning)
            warnings.Add($"The MCP TLS certificate '{certificatePath}' expires on {notAfter:u} (in {(int)Math.Ceiling((notAfter - now).TotalDays)} days); renew it soon.");

        if (notBefore > now)
            warnings.Add($"The MCP TLS certificate '{certificatePath}' is not valid before {notBefore:u}; clients will reject it until then.");

        return certificate;
    }

    private static string ReadPem(string path, string name)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (IsFileError(e))
        {
            throw Forbidden($"Mcp.{name} '{path}' cannot be read: {e.Message}");
        }
    }

    private static bool IsFileError(Exception e) => e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    private static void ValidatePortCollisions(int port, IJsonRpcConfig rpc, IMetricsConfig? metrics)
    {
        if (port == 0) return;

        // Checked even when JSON-RPC is disabled so enabling it later can never put MCP on the Engine API port.
        CheckCollision(port, rpc.Port, "JsonRpc.Port");
        CheckCollision(port, rpc.WebSocketsPort, "JsonRpc.WebSocketsPort");
        if (rpc.EnginePort is int enginePort) CheckCollision(port, enginePort, "JsonRpc.EnginePort");

        foreach (string additionalUrl in rpc.AdditionalRpcUrls ?? [])
        {
            JsonRpcUrl url;
            try
            {
                url = JsonRpcUrl.Parse(additionalUrl);
            }
            catch (FormatException)
            {
                // Malformed entries are reported by the JSON-RPC runner itself.
                continue;
            }

            CheckCollision(port, url.Port, "JsonRpc.AdditionalRpcUrls");
        }

        if (metrics is { Enabled: true, ExposePort: int exposePort })
            CheckCollision(port, exposePort, "Metrics.ExposePort");
    }

    private static void CheckCollision(int port, int otherPort, string otherName)
    {
        if (port == otherPort)
            throw Conflicting($"Mcp.Port {port} collides with {otherName}; the MCP server needs its own port.");
    }

    private static void RequirePositive(long value, string name)
    {
        if (value <= 0)
            throw Forbidden($"Mcp.{name} must be positive, got {value}.");
    }

    private static InvalidConfigurationException Forbidden(string message) => new(message, ExitCodes.ForbiddenOptionValue);

    private static InvalidConfigurationException Conflicting(string message) => new(message, ExitCodes.ConflictingConfigurations);
}
