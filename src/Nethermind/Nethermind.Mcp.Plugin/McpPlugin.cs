// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Core;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Mcp.Plugin.Tools;
using Module = Autofac.Module;

namespace Nethermind.Mcp.Plugin;

/// <summary>Exposes read-only execution-client tools to AI agents over the Model Context Protocol.</summary>
public class McpPlugin(IMcpConfig config) : INethermindPlugin
{
    /// <inheritdoc/>
    public string Name => "Mcp";

    /// <inheritdoc/>
    public string Description => "Model Context Protocol server exposing read-only chain tools on a loopback listener";

    /// <inheritdoc/>
    public string Author => "Nethermind";

    /// <inheritdoc/>
    public bool Enabled => config.Enabled;

    /// <inheritdoc/>
    public IModule Module => new McpModule();
}

/// <summary>Registers the MCP host, its startup step and the MCP tools.</summary>
public class McpModule : Module
{
    /// <inheritdoc/>
    protected override void Load(ContainerBuilder builder) =>
        builder
            .AddModule(new McpToolsModule())
            .AddSingleton<McpHost>()
            .AddStep(typeof(StartMcpServer));
}
