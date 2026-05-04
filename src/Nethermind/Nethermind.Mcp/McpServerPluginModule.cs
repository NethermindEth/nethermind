// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Autofac.Extensions.DependencyInjection;
using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Mcp.Adapter;
using Nethermind.Mcp.Hosting;
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

        // McpWebHost needs an IServiceProvider over the same scope that owns the tools and
        // resources above (the SDK resolves them through GetService). Autofac's
        // AutofacServiceProvider wraps an ILifetimeScope into an IServiceProvider, so
        // resolutions through the wrapper hit the same Autofac registrations.
        builder.Register(ctx =>
            {
                ILifetimeScope scope = ctx.Resolve<ILifetimeScope>();
                return new McpWebHost(
                    scope.Resolve<IMcpConfig>(),
                    scope.Resolve<ILogManager>(),
                    new AutofacServiceProvider(scope));
            })
            .AsSelf()
            .SingleInstance();
    }
}
