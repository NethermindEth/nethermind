// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Mcp.Plugin;

/// <inheritdoc cref="IMcpConfig"/>
public class McpConfig : IMcpConfig
{
    /// <inheritdoc/>
    public bool Enabled { get; set; }
    /// <inheritdoc/>
    public string Host { get; set; } = "127.0.0.1";
    /// <inheritdoc/>
    public int Port { get; set; } = 8555;
    /// <inheritdoc/>
    public string? AuthTokenFile { get; set; }
    /// <inheritdoc/>
    public string[] AllowedOrigins { get; set; } = [];
    /// <inheritdoc/>
    public long MaxRequestBodySize { get; set; } = 256 * 1024;
    /// <inheritdoc/>
    public int MaxConcurrentToolCalls { get; set; } = 4;
    /// <inheritdoc/>
    public int ToolTimeout { get; set; } = 10_000;
    /// <inheritdoc/>
    public int MaxResultSize { get; set; } = 4 * 1024 * 1024;
    /// <inheritdoc/>
    public long MaxLogBlockRange { get; set; } = 1000;
    /// <inheritdoc/>
    public int MaxLogs { get; set; } = 10_000;
    /// <inheritdoc/>
    public long MaxCallGas { get; set; } = 50_000_000;
    /// <inheritdoc/>
    public int MaxCallDataSize { get; set; } = 128 * 1024;
    /// <inheritdoc/>
    public string? TlsCertificatePath { get; set; }
    /// <inheritdoc/>
    public string? TlsCertificateKeyPath { get; set; }
    /// <inheritdoc/>
    public string[] AllowedHosts { get; set; } = [];
    /// <inheritdoc/>
    public long MaxIndexedLogBlockRange { get; set; } = 1_000_000;
    /// <inheritdoc/>
    public int MaxTraceCalls { get; set; } = 2000;
    /// <inheritdoc/>
    public bool EnableTracing { get; set; } = true;
}
