// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.IndexTables;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Exceptions;
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
    [Test]
    public async Task BlockProcessing_with_Eip8304_and_BAL_records_index_table_commitment_in_BAL()
    {
        OverridableReleaseSpec spec = new(Amsterdam.Instance)
        {
            IsEip8304Enabled = true,
            Eip8304ContractAddress = TestItem.AddressA,
            IsEip7928Enabled = true
        };

        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder =>
            builder.AddSingleton<ISpecProvider>(new TestSpecProvider(spec)));

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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(0, (long)processed.Number, processed.Hash), Is.Not.Null);
            Assert.That(processed.GeneratedBlockAccessList, Is.Not.Null);
            Assert.That(processed.GeneratedBlockAccessList!.HasAccount(TestItem.AddressA), Is.True, "BAL should record the access to Eip8304ContractAddress");
        }
    }

    [Test]
    public async Task BlockProcessing_failed_block_rolls_back_table_entries()
    {
        OverridableReleaseSpec spec = new(Amsterdam.Instance)
        {
            IsEip8304Enabled = true,
            Eip8304ContractAddress = TestItem.AddressA
        };

        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder =>
            builder.AddSingleton<ISpecProvider>(new TestSpecProvider(spec)));

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
        OverridableReleaseSpec spec = new(Amsterdam.Instance)
        {
            IsEip8304Enabled = true,
            Eip8304ContractAddress = TestItem.AddressA
        };

        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder =>
            builder.AddSingleton<ISpecProvider>(new TestSpecProvider(spec)));

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
}
