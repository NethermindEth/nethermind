// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using ModelContextProtocol.Server;
using Nethermind.Mcp.Adapter;
using Nethermind.Mcp.Dto;

namespace Nethermind.Mcp.Tools;

[McpServerToolType]
public sealed class NodeHealthTools(INethermindNodeAdapter adapter)
{
    [McpServerTool(Name = "get_node_health"), Description("Composite health: sync, peers, memory, uptime, GC stats.")]
    public NodeHealthDto GetNodeHealth() => adapter.GetNodeHealth();
}
