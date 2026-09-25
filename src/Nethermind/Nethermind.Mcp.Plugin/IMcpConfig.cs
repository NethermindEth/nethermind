// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Mcp.Plugin;

/// <summary>Configuration of the Model Context Protocol (MCP) server exposing read-only node tools.</summary>
[ConfigCategory(Description = "Configuration of the Model Context Protocol (MCP) server that exposes read-only execution-client tools over Streamable HTTP on a loopback listener.")]
public interface IMcpConfig : IConfig
{
    /// <summary>Gets or sets whether the MCP server is started.</summary>
    [ConfigItem(Description = "Whether to start the MCP server.", DefaultValue = "false")]
    bool Enabled { get; set; }

    /// <summary>Gets or sets the IP address the MCP listener binds to.</summary>
    [ConfigItem(Description = "The IP address the MCP listener binds to. A non-loopback address enables remote mode, which requires `TlsCertificatePath`, `TlsCertificateKeyPath`, `AuthTokenFile` and `AllowedHosts`.", DefaultValue = "127.0.0.1")]
    string Host { get; set; }

    /// <summary>Gets or sets the TCP port of the MCP listener.</summary>
    [ConfigItem(Description = "The TCP port of the MCP listener. The endpoint is served at `http://<Host>:<Port>/mcp`. Must differ from every JSON-RPC, Engine API, WebSocket and metrics port.", DefaultValue = "8555")]
    int Port { get; set; }

    /// <summary>Gets or sets the path to a file holding the bearer token clients must present.</summary>
    [ConfigItem(Description = "Path to a file whose trimmed content is the bearer token MCP clients must send as `Authorization: Bearer <token>`. The token must be at least 32 characters. If empty, no authentication is required, and access is limited to local processes by the loopback binding.", DefaultValue = "null")]
    string? AuthTokenFile { get; set; }

    /// <summary>Gets or sets the browser origins allowed to call the MCP endpoint.</summary>
    [ConfigItem(Description = "Browser origins (for example, `http://localhost:6274`) allowed to call the MCP endpoint. Requests without an `Origin` header are allowed; requests with any other origin are rejected with HTTP 403.", DefaultValue = "[]")]
    string[] AllowedOrigins { get; set; }

    /// <summary>Gets or sets the maximum size of an MCP HTTP request body, in bytes.</summary>
    [ConfigItem(Description = "The maximum size of an MCP HTTP request body, in bytes.", DefaultValue = "262144")]
    long MaxRequestBodySize { get; set; }

    /// <summary>Gets or sets the maximum number of tool calls executing at the same time.</summary>
    [ConfigItem(Description = "The maximum number of tool calls executing concurrently. Further calls fail immediately with a `resource_exhausted` error.", DefaultValue = "4")]
    int MaxConcurrentToolCalls { get; set; }

    /// <summary>Gets or sets the wall-clock limit for a single tool call, in milliseconds.</summary>
    [ConfigItem(Description = "The wall-clock limit for a single tool call, in milliseconds. Must not exceed `JsonRpc.Timeout`.", DefaultValue = "10000")]
    int ToolTimeout { get; set; }

    /// <summary>Gets or sets the maximum size of a serialized tool result, in bytes.</summary>
    [ConfigItem(Description = "The maximum size of a serialized tool result, in bytes. Larger results fail with a `resource_exhausted` error.", DefaultValue = "4194304")]
    int MaxResultSize { get; set; }

    /// <summary>Gets or sets the maximum number of blocks a `get_logs` query may span.</summary>
    [ConfigItem(Description = "The maximum number of blocks a `get_logs` query may span, inclusive.", DefaultValue = "1000")]
    long MaxLogBlockRange { get; set; }

    /// <summary>Gets or sets the maximum number of logs a `get_logs` query may return.</summary>
    [ConfigItem(Description = "The maximum number of logs a `get_logs` query may return. Queries matching more fail with a `resource_exhausted` error.", DefaultValue = "10000")]
    int MaxLogs { get; set; }

    /// <summary>Gets or sets the maximum gas a `call` tool invocation may use.</summary>
    [ConfigItem(Description = "The maximum gas a `call` tool invocation may use. It is also capped by `JsonRpc.GasCap`.", DefaultValue = "50000000")]
    long MaxCallGas { get; set; }

    /// <summary>Gets or sets the maximum size of `call` input data, in bytes.</summary>
    [ConfigItem(Description = "The maximum size of the `call` tool input data, in bytes.", DefaultValue = "131072")]
    int MaxCallDataSize { get; set; }

    /// <summary>Gets or sets the path to the PEM certificate served in remote mode.</summary>
    [ConfigItem(Description = "Path to the PEM-encoded TLS certificate (chain) served by the MCP listener. Required in remote mode; also enables HTTPS on loopback.", DefaultValue = "null")]
    string? TlsCertificatePath { get; set; }

    /// <summary>Gets or sets the path to the PEM private key of <see cref="TlsCertificatePath"/>.</summary>
    [ConfigItem(Description = "Path to the PEM-encoded private key of `TlsCertificatePath`.", DefaultValue = "null")]
    string? TlsCertificateKeyPath { get; set; }

    /// <summary>Gets or sets the additional <c>Host</c> header values accepted, such as the node's DNS name in remote mode.</summary>
    [ConfigItem(Description = "Additional `Host` header values (host or host:port, for example `node.example.com`) the MCP endpoint accepts besides loopback names. Required in remote mode.", DefaultValue = "[]")]
    string[] AllowedHosts { get; set; }

    /// <summary>Gets or sets the maximum <c>get_logs</c> block span when the node's log index covers the range.</summary>
    [ConfigItem(Description = "The maximum number of blocks a log query may span when the node's log index (`LogIndex.Enabled`) covers the whole range. Used instead of `MaxLogBlockRange` for indexed ranges.", DefaultValue = "1000000")]
    long MaxIndexedLogBlockRange { get; set; }

    /// <summary>Gets or sets the maximum number of call frames a trace tool returns.</summary>
    [ConfigItem(Description = "The maximum number of call frames returned by `trace_transaction`; deeper or wider call trees are truncated and flagged.", DefaultValue = "2000")]
    int MaxTraceCalls { get; set; }
}
