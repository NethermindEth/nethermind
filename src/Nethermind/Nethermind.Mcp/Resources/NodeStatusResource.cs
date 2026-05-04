// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Server;
using Nethermind.Mcp.Adapter;
using Nethermind.Mcp.Dto;

namespace Nethermind.Mcp.Resources;

[McpServerResourceType]
public sealed class NodeStatusResource(INethermindNodeAdapter adapter)
{
    [McpServerResource(UriTemplate = "nethermind://node/status"), Description("Composite node status — sync, peers, health, version.")]
    public NodeStatusResourceDto Read() => new(
        Sync: adapter.GetSyncStatus(),
        Health: adapter.GetNodeHealth(),
        Version: adapter.GetNodeVersion());
}

public sealed record NodeStatusResourceDto(
    SyncStatusDto Sync,
    NodeHealthDto Health,
    NodeVersionDto Version);
