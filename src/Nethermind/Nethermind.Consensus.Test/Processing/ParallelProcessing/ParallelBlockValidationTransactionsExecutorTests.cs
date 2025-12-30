// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Autofac.Core.Lifetime;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Processing.ParallelProcessing;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.TransactionProcessing;
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

    public static IEnumerable<TestCaseData> SimpleBlocksTests
    {
        get
        {
            yield return Test("1 Transaction", [Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0)]);
            yield return Test("3 Transactions, nonce dependency",
            [
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0),
                Tx(TestItem.PrivateKeyA, TestItem.AddressC, 1),
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 2)
            ]);
        }
    }

    public static IEnumerable<TestCaseData> FailedBlocksTests
    {
        get
        {
            yield return Test([Tx(TestItem.PrivateKeyA, TestItem.AddressB, 1)], TransactionResult.WrongTransactionNonce);
            yield return Test([Tx(TestItem.PrivateKeyF, TestItem.AddressB, 0)], TransactionResult.InsufficientSenderBalance);
            yield return Test(
            [
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0),
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 2)
            ], TransactionResult.WrongTransactionNonce, "on dependent transaction");
            yield return Test(
            [
                Tx(TestItem.PrivateKeyA, TestItem.AddressB, 0, 1.Ether()),
                Tx(TestItem.PrivateKeyB, TestItem.AddressC, 0, 1.Ether() / 2),
                Tx(TestItem.PrivateKeyB, TestItem.AddressC, 1, 1.Ether()),
            ], TransactionResult.InsufficientSenderBalance, "on dependent transaction");

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

    private static TestCaseData Test(string name, Transaction[] transactions) => new([transactions]) { TestName = name };

    private static TestCaseData Test(Transaction[] transactions, TransactionResult expected, string name = "", [CallerArgumentExpression(nameof(expected))] string error = "") =>
        new([transactions, expected]) { TestName = $"{transactions.Length} Transactions, {error.Replace(nameof(TransactionResult) + ".", "")}:{name}" };

    [TestCaseSource(nameof(SimpleBlocksTests))]
    public async Task Simple_blocks(Transaction[] transactions)
    {
        using ParallelTestBlockchain parallel = await ParallelTestBlockchain.Create(BuildConfig(true));
        using ParallelTestBlockchain single = await ParallelTestBlockchain.Create(BuildConfig(false));
        Block block = await parallel.AddBlock(transactions);
        Block singleBlock = await single.AddBlock(transactions);

        Assert.Multiple(() =>
        {
            Assert.That(block.Transactions, Has.Length.EqualTo(transactions.Length));
            Assert.That(singleBlock.Transactions, Has.Length.EqualTo(transactions.Length));
            Assert.That(block.Header.GasUsed, Is.EqualTo(singleBlock.Header.GasUsed));
            Assert.That(block.Header.StateRoot, Is.EqualTo(singleBlock.Header.StateRoot));
        });
    }

    [TestCaseSource(nameof(FailedBlocksTests))]
    public async Task Failed_blocks(Transaction[] transactions, TransactionResult expected)
    {
        using ParallelTestBlockchain parallel = await ParallelTestBlockchain.Create(BuildConfig(true));
        BlockHeader head = parallel.BlockTree.Head!.Header;
        Block block = Build.A.Block.WithTransactions(transactions).WithParent(head).TestObject;
        IReleaseSpec releaseSpec = parallel.SpecProvider.GetSpec(block.Header);
        using IDisposable scope = parallel.MainProcessingContext.WorldState.BeginScope(head);
        TransactionResult result = TransactionResult.Ok;
        try
        {
            parallel.MainProcessingContext.BlockProcessor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, releaseSpec);
        }
        catch (InvalidTransactionException e)
        {
            result = e.Reason;
        }

        Assert.That(result, Is.EqualTo(expected));
    }

    private static IBlocksConfig BuildConfig(bool parallel) =>
        new BlocksConfig
        {
            MinGasPrice = 0,
            PreWarmStateOnBlockProcessing = !parallel,
            ParallelBlockProcessing = parallel
        };
}
