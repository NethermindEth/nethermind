// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.IndexTables;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Messages;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.State;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Crypto;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.IndexTables;

[Parallelizable(ParallelScope.All)]
public class IndexTableIntegrationTests
{
    private static readonly Address IndexContract = TestItem.AddressF;

    // Runtime code of the index contract from https://eips.ethereum.org/EIPS/eip-8304#bytecode
    internal static readonly byte[] IndexContractCode = Bytes.FromHexString(
        "3373fffffffffffffffffffffffffffffffffffffffe1460605760403603605c576020358060801c605c576104008160048104430304828202925f35818106605c5704908103196103ff10605c570601548015605c575f5260205ff35b5f5ffd5b604035602035610400818102915f350406015500");

    private static async Task<BasicTestBlockchain> CreateChain(bool enableBal = false, ISpecProvider? specProvider = null)
    {
        specProvider ??= new TestSpecProvider(new OverridableReleaseSpec(Amsterdam.Instance)
        {
            IsEip8304Enabled = true,
            Eip8304ContractAddress = IndexContract,
            IsEip7928Enabled = enableBal
        });

        return await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(specProvider)
            .AddScoped<IGenesisPostProcessor, IWorldState>(worldState => new FunctionalGenesisPostProcessor(_ =>
            {
                worldState.CreateAccount(IndexContract, 0);
                worldState.InsertCode(IndexContract, IndexContractCode, specProvider.GenesisSpec);
                worldState.RecalculateStateRoot();
            })));
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
            Assert.That(processed.GeneratedBlockAccessList!.HasAccount(IndexContract), Is.True, "BAL should record the access to Eip8304ContractAddress");
        }
    }

    [Test]
    public async Task BlockProcessing_stores_table_roots_in_index_contract([Values] bool enableBal)
    {
        using BasicTestBlockchain chain = await CreateChain(enableBal);

        await chain.BuildSomeBlocks(4);

        List<IndexEntry>[] tables = new List<IndexEntry>[5];
        for (int number = 0; number < tables.Length; number++)
        {
            Block block = chain.BlockTree.FindBlock((ulong)number, BlockTreeLookupOptions.RequireCanonical)!;
            tables[number] = [];
            IndexEntryGenerator.GenerateEntries(block.Header, block.Transactions, chain.ReceiptStorage.Get(block), block.Header.ParentHash, tables[number]);
            tables[number].Sort();
        }

        List<IndexEntry> level1Table = [.. tables[0], .. tables[1], .. tables[2], .. tables[3]];
        level1Table.Sort();

        BlockHeader head = chain.BlockTree.Head!.Header;
        Assert.That(head.Number, Is.EqualTo(4UL));
        using (Assert.EnterMultipleScope())
        {
            for (int number = 1; number < tables.Length; number++)
            {
                Assert.That(GetStoredRoot(chain, head, tableSize: 1, firstBlock: number), Is.EqualTo(SszRoot(tables[number])), $"level-0 root of block {number}");
            }

            Assert.That(GetStoredRoot(chain, head, tableSize: 4, firstBlock: 0), Is.EqualTo(SszRoot(level1Table)), "level-1 root of blocks 0-3");
        }
    }

    private static byte[] GetStoredRoot(BasicTestBlockchain chain, BlockHeader header, int tableSize, int firstBlock)
    {
        UInt256 slot = (UInt256)(tableSize * Eip8304Constants.TablesPerLevel + firstBlock / tableSize % Eip8304Constants.TablesPerLevel);
        chain.StateReader.GetStorage(header, IndexContract, slot, out UInt256 word);
        return word.ToBigEndian();
    }

    private static byte[] SszRoot(List<IndexEntry> sortedEntries)
    {
        byte[] root = new byte[32];
        IndexTableRootCalculator.ComputeRoot(sortedEntries).ToLittleEndian(root);
        return root;
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
    public async Task BlockProcessing_failed_sibling_keeps_tables_of_processed_block([Values] bool enableBal)
    {
        using BasicTestBlockchain chain = await CreateChain(enableBal);

        IIndexTableStore store = chain.Container.Resolve<IIndexTableStore>();
        Block parent = chain.BlockTree.Head!;

        Block processed = chain.BranchProcessor.Process(
            parent.Header,
            [Build.A.Block.WithParent(parent).TestObject],
            ProcessingOptions.NoValidation,
            NullBlockTracer.Instance)[0];
        IReadOnlyList<IndexEntry>? table = store.Get(0, (long)processed.Number, processed.Hash);

        // The nonce is invalid, so the sibling fails before its own tables are committed.
        Block failingSibling = Build.A.Block
            .WithParent(parent)
            .WithExtraData([1])
            .WithTransactions(Build.A.Transaction.WithNonce(1000).SignedAndResolved(TestItem.PrivateKeyA).TestObject)
            .TestObject;

        Assert.Throws<InvalidTransactionException>(() => chain.BranchProcessor.Process(
            parent.Header,
            [failingSibling],
            ProcessingOptions.None,
            NullBlockTracer.Instance));

        Assert.That(store.Get(0, (long)processed.Number, processed.Hash), Is.Not.Null.And.SameAs(table));
    }

    [Test]
    public async Task BlockProcessing_rejects_block_when_deployed_index_contract_fails([Values] bool enableBal)
    {
        PrivateKey deployer = TestItem.PrivateKeyD;
        Address contract = ContractAddress.From(deployer.Address, 0);
        using BasicTestBlockchain chain = await CreateChain(specProvider: new TestSpecProvider(new OverridableReleaseSpec(Amsterdam.Instance)
        {
            IsEip8304Enabled = true,
            Eip8304ContractAddress = contract,
            IsEip7928Enabled = enableBal
        }));

        await chain.AddFunds(deployer.Address, 1.Ether);
        Block parent = chain.BlockTree.Head!;
        // The block deploying the contract already ends with the failing system call.
        Block block = Build.A.Block
            .WithParent(parent)
            .WithTransactions(Build.A.Transaction
                .WithCode(Prepare.EvmCode.ForInitOf([(byte)Instruction.INVALID]).Done)
                .WithGasLimit(1_000_000)
                .WithGasPrice(parent.BaseFeePerGas)
                .SignedAndResolved(deployer)
                .TestObject)
            .TestObject;

        InvalidBlockException exception = Assert.Throws<InvalidBlockException>(() => chain.BranchProcessor.Process(
            parent.Header,
            [block],
            ProcessingOptions.NoValidation,
            NullBlockTracer.Instance))!;

        Assert.That(exception.Message, Does.Contain(BlockErrorMessages.IndexContractFailed));
    }

    [Test]
    public async Task BlockProcessing_recovers_missing_tables_on_empty_store_restart()
    {
        using BasicTestBlockchain chain = await CreateChain();

        IndexTableStore store = chain.Container.Resolve<IndexTableStore>();

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
            Eip8304ContractAddress = IndexContract
        };

        ulong activationTimestamp = ulong.MaxValue;
        ISpecProvider specProvider = new OverridableSpecProvider(
            new TestSpecProvider(Amsterdam.Instance),
            (spec, activation) => activation.Timestamp >= activationTimestamp ? postSpec : preSpec);

        using BasicTestBlockchain chain = await CreateChain(specProvider: specProvider);
        IIndexTableStore store = chain.Container.Resolve<IIndexTableStore>();

        Block parent = chain.BlockTree.Head!;
        activationTimestamp = parent.Timestamp + 1000;
        Block b1 = Build.A.Block.WithParent(parent).WithTimestamp(parent.Timestamp + 10).TestObject;
        Block b2 = Build.A.Block.WithParent(b1).WithTimestamp(parent.Timestamp + 20).TestObject;
        Block b3 = Build.A.Block.WithParent(b2).WithTimestamp(parent.Timestamp + 30).TestObject;
        Block block4 = Build.A.Block.WithParent(b3).WithTimestamp(parent.Timestamp + 1050).TestObject;
        foreach (Block ancestor in (Block[])[b1, b2, b3])
        {
            chain.BlockTree.SuggestBlock(ancestor, BlockTreeSuggestOptions.None);
        }

        Block[] processed = chain.BranchProcessor.Process(parent.Header, [b1, b2, b3, block4], ProcessingOptions.NoValidation, NullBlockTracer.Instance);

        Assert.That(store.Get(1, 0, processed[3].Hash), Is.Null, "Level-1 table covering block 0 should not be published because fork was inactive at block 0");
    }
}
