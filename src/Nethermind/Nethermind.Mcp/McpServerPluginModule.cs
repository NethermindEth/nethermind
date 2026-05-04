// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Core;
using Nethermind.Mcp.Adapter;
using Nethermind.Mcp.Resources;
using Nethermind.Mcp.Tools;

namespace Nethermind.Mcp;

public sealed class McpServerPluginModule : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        base.Load(builder);

        builder
            .AddSingleton<INethermindNodeAdapter, NethermindNodeAdapter>()
            .AddSingleton<ConfigRedactor>()
            .AddSingleton<NodeStatusTools>()
            .AddSingleton<NodeHealthTools>()
            .AddSingleton<ChainQueryTools>()
            .AddSingleton<NodeStatusResource>()
            .AddSingleton<NodeConfigResource>();
    }
}
