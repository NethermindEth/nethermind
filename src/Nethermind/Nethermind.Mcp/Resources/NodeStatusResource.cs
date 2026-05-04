// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nethermind.Mcp.Adapter;
using Nethermind.Mcp.Dto;

namespace Nethermind.Mcp.Resources;

[McpServerResourceType]
public sealed class NodeStatusResource(INethermindNodeAdapter adapter)
{
    /// <summary>
    /// Resource entry point exposed to the MCP SDK. The SDK reflects on this method's
    /// return type to pick a content shape; arbitrary records aren't supported, so we
    /// emit a <see cref="TextResourceContents"/> with the JSON-serialized DTO. Tests that
    /// want the typed DTO should call <see cref="BuildModel"/> directly.
    /// </summary>
    [McpServerResource(UriTemplate = "nethermind://node/status"), Description("Composite node status — sync, peers, health, version.")]
    public TextResourceContents Read() => new()
    {
        Uri = "nethermind://node/status",
        MimeType = "application/json",
        Text = JsonSerializer.Serialize(BuildModel(), JsonOptions),
    };

    /// <summary>
    /// Builds the structured DTO consumed by unit tests. The serialized form is what
    /// flows through MCP; this method gives tests a strongly-typed view without parsing
    /// JSON round-trips.
    /// </summary>
    public NodeStatusResourceDto BuildModel() => new(
        Sync: adapter.GetSyncStatus(),
        Health: adapter.GetNodeHealth(),
        Version: adapter.GetNodeVersion());

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

public sealed record NodeStatusResourceDto(
    SyncStatusDto Sync,
    NodeHealthDto Health,
    NodeVersionDto Version);
