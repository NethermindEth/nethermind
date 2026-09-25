// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.JsonRpc;
using Nethermind.Monitoring.Config;

namespace Nethermind.Mcp.Plugin;

/// <summary>Validates <see cref="IMcpConfig"/> against itself and the other node listeners before the MCP server starts.</summary>
internal static class McpConfigValidator
{
    internal const int MinAuthTokenLength = 32;

    /// <summary>Throws when the MCP configuration is invalid or conflicts with another node listener.</summary>
    /// <param name="config">The MCP configuration.</param>
    /// <param name="rpc">The JSON-RPC configuration, used for port collisions and the timeout and gas caps.</param>
    /// <param name="metrics">The metrics configuration, used for the Prometheus exposure port collision.</param>
    /// <exception cref="InvalidConfigurationException">The configuration is invalid.</exception>
    public static void Validate(IMcpConfig config, IJsonRpcConfig rpc, IMetricsConfig? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(rpc);

        ParseLoopbackAddress(config.Host);

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

        if (config.ToolTimeout > rpc.Timeout)
            throw Conflicting($"Mcp.ToolTimeout ({config.ToolTimeout} ms) must not exceed JsonRpc.Timeout ({rpc.Timeout} ms).");

        if (rpc.GasCap.IsGasCapped() && (ulong)config.MaxCallGas > rpc.GasCap!.Value)
            throw Conflicting($"Mcp.MaxCallGas ({config.MaxCallGas}) must not exceed JsonRpc.GasCap ({rpc.GasCap.Value}).");

        NormalizeOrigins(config.AllowedOrigins);
        LoadAuthToken(config.AuthTokenFile);
    }

    /// <summary>Parses <paramref name="host"/> as a loopback IP address literal.</summary>
    /// <exception cref="InvalidConfigurationException">The value is not a loopback IP address.</exception>
    internal static IPAddress ParseLoopbackAddress(string? host)
    {
        if (!IPAddress.TryParse(host, out IPAddress? address))
            throw Forbidden($"Mcp.Host must be a loopback IP address such as 127.0.0.1 or ::1, got '{host}'.");

        if (!IPAddress.IsLoopback(address))
            throw Forbidden($"Mcp.Host must be a loopback address; remote access is not supported, got '{host}'.");

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

    /// <summary>Reads the bearer token from <paramref name="path"/>, or returns <see langword="null"/> when no file is configured.</summary>
    /// <exception cref="InvalidConfigurationException">The file cannot be read or the token is shorter than <see cref="MinAuthTokenLength"/>.</exception>
    internal static string? LoadAuthToken(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        string token;
        try
        {
            token = File.ReadAllText(path).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw Forbidden($"Mcp.AuthTokenFile '{path}' cannot be read: {e.Message}");
        }

        if (token.Length < MinAuthTokenLength)
            throw Forbidden($"Mcp.AuthTokenFile '{path}' must contain a token of at least {MinAuthTokenLength} characters.");

        return token;
    }

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
