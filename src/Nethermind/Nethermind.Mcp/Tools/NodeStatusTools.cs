// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Server;
using Nethermind.Mcp.Adapter;
using Nethermind.Mcp.Dto;

namespace Nethermind.Mcp.Tools;

[McpServerToolType]
public sealed class NodeStatusTools(INethermindNodeAdapter adapter)
{
    [McpServerTool(Name = "get_sync_status"), Description("Returns sync state, peer count, and blocks-behind.")]
    public SyncStatusDto GetSyncStatus() => adapter.GetSyncStatus();

    [McpServerTool(Name = "get_node_version"), Description("Returns Nethermind, .NET runtime, and OS versions.")]
    public NodeVersionDto GetNodeVersion() => adapter.GetNodeVersion();
}
