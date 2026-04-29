// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.State;
using Nethermind.State.Flat.ScopeProvider;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class WorldStateDbDeciderModuleTests
{
    [TestCase(false, Description = "Flat disabled → patricia backend selected")]
    [TestCase(true, Description = "Flat enabled, fresh DB → flat backend selected")]
    public void IWorldStateManager_ResolvesToCorrectBackend(bool flatEnabled)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .Intercept<IFlatDbConfig>((cfg) => { cfg.Enabled = flatEnabled; })
            .Build();

        IWorldStateManager worldStateManager = container.Resolve<IWorldStateManager>();

        if (flatEnabled)
            worldStateManager.Should().BeOfType<FlatWorldStateManager>();
        else
            worldStateManager.Should().NotBeOfType<FlatWorldStateManager>();
    }
}
