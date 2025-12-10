// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Autofac.Core.Lifetime;
using Nethermind.Config;
using Nethermind.Consensus.Processing.ParallelProcessing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing.ParallelProcessing;

public class ParallelBlockValidationTransactionsExecutorTests
{
    public class ParallelTestBlockchain(IBlocksConfig blocksConfig) : TestBlockchain
    {
        public static async Task<ParallelTestBlockchain> Create(IBlocksConfig blocksConfig, Action<ContainerBuilder> configurer = null)
        {
            ParallelTestBlockchain chain = new(blocksConfig);
            await chain.Build(configurer);
            return chain;
        }

        protected override Task AddBlocksOnStart() => Task.CompletedTask;

        protected override IEnumerable<IConfig> CreateConfigs() => [blocksConfig];

        protected override ContainerBuilder ConfigureContainer(ContainerBuilder builder, IConfigProvider configProvider) =>
            base.ConfigureContainer(builder, configProvider)
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Osaka.Instance));
    }

    [Test]
    public async Task Simple_block()
    {
        using ParallelTestBlockchain blockchain = await ParallelTestBlockchain.Create(BuildConfig(true));
        Block block = await blockchain.AddBlock(
            Build.A.Transaction.To(TestItem.AddressB).WithNonce(0).WithChainId(blockchain.SpecProvider.ChainId)
                .SignedAndResolved(TestItem.PrivateKeyA, false).TestObject);
            //Build.A.Transaction.To(TestItem.AddressB).WithNonce(1).WithChainId(blockchain.SpecProvider.ChainId).SignedAndResolved(TestItem.PrivateKeyA, false).TestObject);
        Assert.That(block.Transactions, Has.Length.EqualTo(2));
    }

    private static IBlocksConfig BuildConfig(bool parallel) =>
        new BlocksConfig
        {
            MinGasPrice = 0,
            PreWarmStateOnBlockProcessing = !parallel,
            ParallelBlockProcessing = parallel
        };
}
