// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.IndexTables;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.IndexTables;

[Parallelizable(ParallelScope.All)]
public class IndexTableIntegrationTests
{
    private static async Task<BasicTestBlockchain> CreateChain(bool enableBal = false, ISpecProvider? specProvider = null)
    {
        specProvider ??= new TestSpecProvider(new OverridableReleaseSpec(Amsterdam.Instance)
        {
            IsEip8304Enabled = true,
            Eip8304ContractAddress = TestItem.AddressA,
            IsEip7928Enabled = enableBal
        });

        return await BasicTestBlockchain.Create(builder =>
            builder.AddSingleton<ISpecProvider>(specProvider));
    }

    [Test]
    public async Task BlockProcessing_with_Eip8304_and_BAL_records_index_table_commitment_in_BAL()
    {
        using BasicTestBlockchain chain = await CreateChain(enableBal: true);

        IIndexTableStore store = chain.Container.Resolve<IIndexTableStore>();
        Block parent = chain.BlockTree.Head!;
        Block block1 = Build.A.Block
            .WithParent(parent)
            .WithAuthor(TestItem.AddressB)
            .TestObject;

        Block processed = chain.BranchProcessor.Process(
            parent.Header,
            [block1],
            ProcessingOptions.NoValidation,
            NullBlockTracer.Instance)[0];

        Assert.That(processed.GeneratedBlockAccessList, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(0, (long)processed.Number, processed.Hash), Is.Not.Null);
            Assert.That(processed.GeneratedBlockAccessList!.HasAccount(TestItem.AddressA), Is.True, "BAL should record the access to Eip8304ContractAddress");
        }
    }

    [Test]
    public async Task BlockProcessing_failed_block_rolls_back_table_entries()
    {
        using BasicTestBlockchain chain = await CreateChain();

        IIndexTableStore store = chain.Container.Resolve<IIndexTableStore>();
        Block parent = chain.BlockTree.Head!;

        Block failingBlock = Build.A.Block
            .WithParent(parent)
            .WithStateRoot(TestItem.KeccakB)
            .TestObject;

        Assert.Throws<InvalidBlockException>(() => chain.BranchProcessor.Process(
            parent.Header,
            [failingBlock],
            ProcessingOptions.None,
            NullBlockTracer.Instance));

        Assert.That(store.Get(0, (long)failingBlock.Number, failingBlock.Hash), Is.Null);
    }

    [Test]
    public async Task BlockProcessing_recovers_missing_tables_on_empty_store_restart()
    {
        using BasicTestBlockchain chain = await CreateChain();

        IIndexTableStore store = chain.Container.Resolve<IIndexTableStore>();

        await chain.BuildSomeBlocks(3);

        for (int lvl = 0; lvl < 5; lvl++)
        {
            for (long b = 0; b <= 3; b++)
            {
                store.Remove(lvl, b);
            }
        }

        Assert.That(store.Get(0, 1), Is.Null, "Store should be empty after simulated restart");

        await chain.BuildSomeBlocks(1);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(1, 0), Is.Not.Null, "Level-1 table for blocks 0-3 should be built and stored");
            Assert.That(store.Get(0, 1), Is.Not.Null, "Missing level-0 table for block 1 should be recovered");
        }
    }

    [Test]
    public async Task BAL_manager_retains_handler_scope_without_cross_dispatch()
    {
        using BasicTestBlockchain chain = await CreateChain(enableBal: true);
        IIndexTableStore store = chain.Container.Resolve<IIndexTableStore>();

        ILifetimeScope processingScope = ((Nethermind.Init.Modules.MainProcessingContext)chain.MainProcessingContext).LifetimeScope;
        using ILifetimeScope scopeA = processingScope.BeginLifetimeScope();
        using ILifetimeScope scopeB = processingScope.BeginLifetimeScope();

        using IDisposable worldScopeA = scopeA.Resolve<IWorldState>().BeginScope(chain.BlockTree.Head!.Header);
        using IDisposable worldScopeB = scopeB.Resolve<IWorldState>().BeginScope(chain.BlockTree.Head!.Header);

        IBlockAccessListManager balManagerA = scopeA.Resolve<IBlockAccessListManager>();
        IBlockAccessListManager balManagerB = scopeB.Resolve<IBlockAccessListManager>();

        Block blockA = new(Build.A.BlockHeader.WithNumber(10).WithHash(TestItem.KeccakA).TestObject);
        Block blockB = new(Build.A.BlockHeader.WithNumber(10).WithHash(TestItem.KeccakB).TestObject);

        IReleaseSpec specA = chain.SpecProvider.GetSpec(blockA.Header);
        balManagerA.PrepareForProcessing(blockA, specA, ProcessingOptions.None);
        balManagerA.SetBlockExecutionContext(new BlockExecutionContext(blockA.Header, specA));
        balManagerA.Setup(blockA);

        IReleaseSpec specB = chain.SpecProvider.GetSpec(blockB.Header);
        balManagerB.PrepareForProcessing(blockB, specB, ProcessingOptions.None);
        balManagerB.SetBlockExecutionContext(new BlockExecutionContext(blockB.Header, specB));
        balManagerB.Setup(blockB);

        balManagerA.CommitIndexTableRoots(blockA, [], specA, NullTxTracer.Instance);
        balManagerB.CommitIndexTableRoots(blockB, [], specB, NullTxTracer.Instance);

        Hash256 finalHashA = TestItem.KeccakC;
        blockA.Header.Hash = finalHashA;
        balManagerA.UpdateFinalBlockHash(blockA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(0, 10, finalHashA), Is.Not.Null);
            Assert.That(store.Get(0, 10, TestItem.KeccakB), Is.Not.Null);
            Assert.That(store.Get(0, 10, TestItem.KeccakA), Is.Null);
        }
    }

    [Test]
    public async Task Simulation_leaves_root_index_table_store_unchanged()
    {
        using BasicTestBlockchain chain = await CreateChain(enableBal: true);
        IIndexTableStore rootStore = chain.Container.Resolve<IIndexTableStore>();

        ILifetimeScope processingScope = ((Nethermind.Init.Modules.MainProcessingContext)chain.MainProcessingContext).LifetimeScope;
        using ILifetimeScope simScope = processingScope.BeginLifetimeScope(builder =>
        {
            builder.AddSingleton<IIndexTableStore, IndexTableStore>();
            builder.AddSingleton<IIndexTableHandlerFactory, IndexTableHandlerFactory>();
        });

        using IDisposable simWorldScope = simScope.Resolve<IWorldState>().BeginScope(chain.BlockTree.Head!.Header);
        IIndexTableStore simStore = simScope.Resolve<IIndexTableStore>();
        IBlockAccessListManager simBalManager = simScope.Resolve<IBlockAccessListManager>();

        Block block = new(Build.A.BlockHeader.WithNumber(1).WithHash(TestItem.KeccakA).TestObject);
        IReleaseSpec spec = chain.SpecProvider.GetSpec(block.Header);
        simBalManager.PrepareForProcessing(block, spec, ProcessingOptions.None);
        simBalManager.SetBlockExecutionContext(new BlockExecutionContext(block.Header, spec));
        simBalManager.Setup(block);
        simBalManager.CommitIndexTableRoots(block, [], spec, NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(simStore.Get(0, 1, block.Hash), Is.Not.Null);
            Assert.That(rootStore.Get(0, 1, block.Hash), Is.Null);
            Assert.That(rootStore.Get(0, 1), Is.Null);
        }
    }

    [Test]
    public async Task Activation_evaluated_using_branch_ancestor_even_when_uncanonicalized()
    {
        OverridableReleaseSpec preSpec = new(Amsterdam.Instance) { IsEip8304Enabled = false };
        OverridableReleaseSpec postSpec = new(Amsterdam.Instance)
        {
            IsEip8304Enabled = true,
            Eip8304ContractAddress = TestItem.AddressA
        };

        ISpecProvider specProvider = new OverridableSpecProvider(
            new TestSpecProvider(Amsterdam.Instance),
            (spec, activation) => activation.Timestamp >= 1000 ? postSpec : preSpec);

        using BasicTestBlockchain chain = await CreateChain(specProvider: specProvider);
        IIndexTableStore store = chain.Container.Resolve<IIndexTableStore>();

        Block parent = chain.BlockTree.Head!;
        for (ulong i = 1; i <= 3; i++)
        {
            Block b = Build.A.Block.WithParent(parent).WithTimestamp(i * 10).TestObject;
            parent = chain.BranchProcessor.Process(parent.Header, [b], ProcessingOptions.NoValidation, NullBlockTracer.Instance)[0];
        }

        Block block4 = Build.A.Block.WithParent(parent).WithTimestamp(1050).TestObject;
        Block processed4 = chain.BranchProcessor.Process(parent.Header, [block4], ProcessingOptions.NoValidation, NullBlockTracer.Instance)[0];

        Assert.That(store.Get(1, 0, processed4.Hash), Is.Null, "Level-1 table covering block 0 should not be published because fork was inactive at block 0");
    }
}
