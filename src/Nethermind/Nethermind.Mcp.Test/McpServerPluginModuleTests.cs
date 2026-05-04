// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Mcp.Adapter;
using Nethermind.Mcp.Resources;
using Nethermind.Mcp.Tools;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Mcp.Test;

public class McpServerPluginModuleTests
{
    [Test]
    public void Module_registers_all_types()
    {
        ContainerBuilder builder = new();
        builder.RegisterInstance(Substitute.For<INethermindApi>()).As<INethermindApi>();
        builder.RegisterInstance(Substitute.For<IConfigProvider>()).As<IConfigProvider>();
        builder.RegisterModule(new McpServerPluginModule());

        using IContainer container = builder.Build();

        Assert.That(container.Resolve<INethermindNodeAdapter>(), Is.Not.Null);
        Assert.That(container.Resolve<ConfigRedactor>(), Is.Not.Null);
        Assert.That(container.Resolve<NodeStatusTools>(), Is.Not.Null);
        Assert.That(container.Resolve<NodeHealthTools>(), Is.Not.Null);
        Assert.That(container.Resolve<ChainQueryTools>(), Is.Not.Null);
        Assert.That(container.Resolve<NodeStatusResource>(), Is.Not.Null);
        Assert.That(container.Resolve<NodeConfigResource>(), Is.Not.Null);
    }
}
