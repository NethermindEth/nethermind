// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm;
using Nethermind.Init.Modules;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Module;

public class MainProcessingModuleTests
{
    [TestCase(32768, true)]
    [TestCase(0, false)]
    [TestCase(-1, false)]
    public void MainProcessingContext_ShouldUseCachedCodeInfoRepository_OnlyWithAPrecompileCacheBudget(int maxKilobytes, bool expectDecorated)
    {
        using IContainer ctx = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new BlocksConfig { PrecompileCacheMaxKilobytes = maxKilobytes }))
            .Build();

        ICodeInfoRepository repository = (ctx.Resolve<IMainProcessingContext>() as MainProcessingContext)
            .LifetimeScope
            .Resolve<ICodeInfoRepository>();

        Assert.That(repository is PrecompileCachedCodeInfoRepository, Is.EqualTo(expectDecorated), $"resolved {repository.GetType().Name}");
    }
}
