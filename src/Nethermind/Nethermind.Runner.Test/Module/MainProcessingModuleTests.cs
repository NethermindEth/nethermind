// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm;
using Nethermind.Init.Modules;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

public class MainProcessingModuleTests
{
    [Test]
    public void MainProcessingContext_ShouldUseCachedCodeInfoRepository()
    {
        using IContainer ctx = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .Build();

        (ctx.Resolve<IMainProcessingContext>() as MainProcessingContext)
            .LifetimeScope
            .Resolve<ICodeInfoRepository>()
            .Should().BeOfType<CachedCodeInfoRepository>();
    }
}
