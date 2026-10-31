// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Api.Steps;
using Nethermind.Init.Steps;

namespace Nethermind.Mcp.Plugin;

/// <summary>Starts the MCP server once the JSON-RPC modules its tools rent are registered.</summary>
[RunnerStepDependencies(typeof(RegisterRpcModules))]
public class StartMcpServer(McpHost host) : IStep
{
    /// <inheritdoc/>
    /// <remarks>An enabled MCP server that cannot start (invalid config, port taken) aborts node startup.</remarks>
    public bool MustInitialize => true;

    /// <inheritdoc/>
    public Task Execute(CancellationToken cancellationToken) => host.StartAsync(cancellationToken);
}
