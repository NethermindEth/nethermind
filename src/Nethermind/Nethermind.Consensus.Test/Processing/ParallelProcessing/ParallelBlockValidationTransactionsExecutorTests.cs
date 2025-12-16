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
using Nethermind.Crypto;
using Nethermind.Int256;
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

    public static IEnumerable<Transaction[]> SimpleBlocksTests
    {
        get
        {
            yield return [Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0)];
            yield return
            [
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0),
                Tx(TestItem.PrivateKeyA, TestItem.AddressC, 1),
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 2)
            ];
        }
    }

    private static Transaction Tx(PrivateKey from, Address to, UInt256 nonce, UInt256? value = null) =>
        Build.A.Transaction
            .To(to)
            .WithNonce(nonce)
            .WithChainId(BlockchainIds.Mainnet)
            .SignedAndResolved(from, false)
            .WithValue(value ?? 1.Ether())
            .TestObject;

    [TestCaseSource(nameof(SimpleBlocksTests))]
    public async Task Simple_blocks(Transaction[] transactions)
    {
        using ParallelTestBlockchain blockchain = await ParallelTestBlockchain.Create(BuildConfig(true));
        Block block = await blockchain.AddBlock(transactions);
        Assert.That(block.Transactions, Has.Length.EqualTo(transactions.Length));
    }

    private static IBlocksConfig BuildConfig(bool parallel) =>
        new BlocksConfig
        {
            MinGasPrice = 0,
            PreWarmStateOnBlockProcessing = !parallel,
            ParallelBlockProcessing = parallel
        };
}
