// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using ModelContextProtocol.Server;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>Collects the tools of every registered <see cref="IMcpToolSet"/>.</summary>
public sealed class McpToolCatalog(IEnumerable<IMcpToolSet> toolSets)
{
    /// <summary>Creates the descriptors of all tools, rejecting duplicate names.</summary>
    /// <exception cref="InvalidOperationException">Two tool sets declare the same tool name.</exception>
    internal IReadOnlyList<McpServerTool> CreateServerTools()
    {
        List<McpServerTool> tools = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (IMcpToolSet toolSet in toolSets)
        {
            foreach (McpServerTool tool in toolSet.CreateServerTools())
            {
                if (!names.Add(tool.ProtocolTool.Name))
                {
                    throw new InvalidOperationException($"MCP tool '{tool.ProtocolTool.Name}' is declared more than once.");
                }

                tools.Add(tool);
            }
        }

        return tools;
    }
}
