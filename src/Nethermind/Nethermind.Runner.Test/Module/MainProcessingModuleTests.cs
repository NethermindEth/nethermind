// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
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

    [Test]
    public void MainProcessingContext_ShouldKeepBlockCode_UntilThePreWarmerClearsCaches()
    {
        StaticCodeCache rootCodeCache = new(MemoryAllowance.CodeCacheSize);
        using IContainer ctx = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new BlocksConfig()))
            .AddSingleton<ICodeCache>(rootCodeCache)
            .Build();

        ILifetimeScope mainScope = (ctx.Resolve<IMainProcessingContext>() as MainProcessingContext).LifetimeScope;
        BlockCodeCache blockCodeCache = mainScope.Resolve<BlockCodeCache>();
        byte[] bytecode = Guid.NewGuid().ToByteArray();
        ValueHash256 codeHash = ValueKeccak.Compute(bytecode);
        CodeInfo code = new(bytecode);
        blockCodeCache.Set(in codeHash, code);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootCodeCache.Get(in codeHash), Is.SameAs(code), "wraps the root's cache");
            Assert.That(mainScope.Resolve<ICodeCache>(), Is.SameAs(blockCodeCache));
            Assert.That(ctx.Resolve<ICodeCache>(), Is.SameAs(rootCodeCache), "outside block processing");
        }

        // Leaves the block's copy as the only one.
        rootCodeCache.Clear();
        Assert.That(blockCodeCache.Get(in codeHash), Is.SameAs(code));

        mainScope.Resolve<IBlockCachePreWarmer>().ClearCaches();
        Assert.That(blockCodeCache.Get(in codeHash), Is.Null);
    }
}
