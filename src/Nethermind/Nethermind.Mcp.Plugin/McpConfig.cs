// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Mcp.Plugin;

public class McpConfig : IMcpConfig
{
    public bool Enabled { get; set; }
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 8555;
    public string? AuthTokenFile { get; set; }
    public string[] AllowedOrigins { get; set; } = [];
    public long MaxRequestBodySize { get; set; } = 256 * 1024;
    public int MaxConcurrentToolCalls { get; set; } = 4;
    public int ToolTimeout { get; set; } = 10_000;
    public int MaxResultSize { get; set; } = 4 * 1024 * 1024;
    public long MaxLogBlockRange { get; set; } = 1000;
    public int MaxLogs { get; set; } = 10_000;
    public long MaxCallGas { get; set; } = 50_000_000;
    public int MaxCallDataSize { get; set; } = 128 * 1024;
    public string? TlsCertificatePath { get; set; }
    public string? TlsCertificateKeyPath { get; set; }
    public string[] AllowedHosts { get; set; } = [];
    public long MaxIndexedLogBlockRange { get; set; } = 1_000_000;
    public int MaxTraceCalls { get; set; } = 2000;
    public bool EnableTracing { get; set; } = true;
}
