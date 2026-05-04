// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Mcp;

public interface IMcpConfig : IConfig
{
    [ConfigItem(Description = "Whether to enable the MCP server plugin.", DefaultValue = "false")]
    bool Enabled { get; set; }

    [ConfigItem(Description = "Whether to enable the HTTP/SSE MCP transport.", DefaultValue = "true")]
    bool HttpEnabled { get; set; }

    [ConfigItem(Description = "Bind address for the MCP HTTP/SSE listener.", DefaultValue = "127.0.0.1")]
    string HttpHost { get; set; }

    [ConfigItem(Description = "Port for the MCP HTTP/SSE listener.", DefaultValue = "8550")]
    int HttpPort { get; set; }

    [ConfigItem(Description = "If set, requires `Authorization: Bearer <key>` on every MCP request.", DefaultValue = "null")]
    string? ApiKey { get; set; }

    [ConfigItem(Description = "Maximum concurrent MCP tool invocations.", DefaultValue = "4")]
    int MaxConcurrent { get; set; }
}
