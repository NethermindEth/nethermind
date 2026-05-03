// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Mcp;

public class McpConfig : IMcpConfig
{
    public bool Enabled { get; set; } = false;
    public bool HttpEnabled { get; set; } = true;
    public string HttpHost { get; set; } = "127.0.0.1";
    public int HttpPort { get; set; } = 8550;
    public string? ApiKey { get; set; } = null;
    public int MaxConcurrent { get; set; } = 4;
    public string[] EnabledTools { get; set; } = new[] { "*" };
}
