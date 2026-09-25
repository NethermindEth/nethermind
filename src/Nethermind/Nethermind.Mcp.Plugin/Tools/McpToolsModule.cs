// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>Registers the MCP tool implementations.</summary>
public sealed class McpToolsModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);
        builder
            .AddSingleton<McpToolExecutor>()
            .AddSingleton<McpChainProfile>()
            .AddSingleton<McpNodeCapabilities>()
            .AddSingleton<McpTokenMetadata>()
            .AddSingleton<McpToolCatalog>();

        // Every tool set in this assembly is served, so adding a tool set needs no registration change.
        builder.RegisterAssemblyTypes(typeof(McpToolsModule).Assembly)
            .Where(static type => typeof(IMcpToolSet).IsAssignableFrom(type) && type is { IsClass: true, IsAbstract: false })
            .AsSelf()
            .As<IMcpToolSet>()
            .SingleInstance();
    }
}
