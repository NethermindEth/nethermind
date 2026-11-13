// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Ethereum.Test.Base;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm;
using Nethermind.Init.Modules;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

public class PreWarmingTests
{
    [TestCase(null, false, TestName = "No override leaves the precompile cache out")]
    [TestCase(PreWarmMode.None, false, TestName = "None leaves the precompile cache out")]
    [TestCase(PreWarmMode.Block, true, TestName = "Block wires the precompile cache in")]
    public async Task The_precompile_cache_runs_only_with_prewarming(PreWarmMode? preWarming, bool cached)
    {
        ConfigProvider configProvider = new();
        BlockchainTestBase.ApplyPreWarming(configProvider.GetConfig<IBlocksConfig>(), preWarming);
        await using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule(configProvider)).Build();

        ICodeInfoRepository codeInfoRepository = container.Resolve<MainProcessingContext>().LifetimeScope.Resolve<ICodeInfoRepository>();

        Assert.That(codeInfoRepository is PrecompileCachedCodeInfoRepository, Is.EqualTo(cached));
    }
}
