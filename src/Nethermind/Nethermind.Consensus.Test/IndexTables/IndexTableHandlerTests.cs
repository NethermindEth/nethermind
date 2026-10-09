// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.IndexTables;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.IndexTables;

public class IndexTableHandlerTests
{
    /// <summary>
    /// A level-2 table spans 16 blocks and is published <c>16 / 4</c> blocks after the last one,
    /// i.e. block 19 publishes the table covering blocks 0-15.
    /// </summary>
    private const ulong Level2PublicationBlock = 19;

    private static readonly Address IndexContract = TestItem.AddressA;

    [Test]
    public void Higher_level_table_is_rebuilt_from_the_level_below_when_sub_tables_are_missing()
    {
        IndexTableStore store = new();
        BlockTree blockTree = BuildChain(Level2PublicationBlock);
        StoreLevel0Tables(store, blockTree, 0, 16);
        using Harness harness = new(store, blockTree);

        harness.Commit(blockTree.FindBlock(Level2PublicationBlock, BlockTreeLookupOptions.None)!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(2, 0), Is.Not.Null.And.Count.EqualTo(16));
            Assert.That(harness.IsPublished(1, (long)Level2PublicationBlock), Is.True);
            Assert.That(harness.IsPublished(16, 0), Is.True);
        }
    }

    [Test]
    public void Higher_level_table_that_cannot_be_built_throws_InvalidOperationException()
    {
        IndexTableStore store = new();
        BlockTree blockTree = BuildChain(Level2PublicationBlock, headersOnly: true);
        // One block short of the range the level-2 table covers.
        StoreLevel0Tables(store, blockTree, 1, 16);
        using Harness harness = new(store, blockTree);

        Assert.Throws<InvalidOperationException>(() => harness.Commit(HeaderOnlyBlock(blockTree, Level2PublicationBlock)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.IsPublished(1, (long)Level2PublicationBlock), Is.True);
            // The level-2 root must not be committed against an incomplete table.
            Assert.That(harness.IsPublished(16, 0), Is.False);
        }
    }

    [Test]
    public void Publication_block_with_unknown_ancestry_throws_without_committing_the_higher_level_table()
    {
        IndexTableStore store = new();
        using Harness harness = new(store, BuildChain(0));

        // Block 4 publishes the level-1 table over blocks 0-3, but its parent is not in the block tree.
        Assert.Throws<InvalidOperationException>(() => harness.Commit(BuildBlock(4)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(1, 0), Is.Null);
            Assert.That(harness.IsPublished(1, 4), Is.True);
            Assert.That(harness.IsPublished(4, 0), Is.False);
        }
    }

    [Test]
    public void Nothing_is_committed_when_the_fork_is_not_active()
    {
        IndexTableStore store = new();
        using Harness harness = new(store);

        harness.Commit(BuildBlock(Level2PublicationBlock), BuildSpec(enabled: false));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(0, (long)Level2PublicationBlock), Is.Null);
            Assert.That(harness.IsPublished(1, (long)Level2PublicationBlock), Is.False);
        }
    }

    [Test]
    public void Activation_boundary_transition_skips_pre_activation_tables()
    {
        IndexTableStore store = new();
        IReleaseSpec activeSpec = BuildSpec();
        CustomSpecProvider specProvider = new(((ForkActivation)0, BuildSpec(enabled: false)), ((ForkActivation)100, activeSpec));
        BlockTree blockTree = BuildChain(104);
        using Harness harness = new(store, blockTree, specProvider);

        // Block 100: candidate level-1 table covers 96-99 (firstBlock = 96), which was pre-activation
        harness.Commit(blockTree.FindBlock(100, BlockTreeLookupOptions.None)!, activeSpec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(0, 100), Is.Not.Null);
            Assert.That(store.Get(1, 96), Is.Null);
            Assert.That(harness.IsPublished(1, 100), Is.True);
            Assert.That(harness.IsPublished(4, 96), Is.False);
        }

        StoreLevel0Tables(store, blockTree, 100, 4);

        // Block 104 publishes level-1 covering 100-103
        harness.Commit(blockTree.FindBlock(104, BlockTreeLookupOptions.None)!, activeSpec);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(1, 100), Is.Not.Null.And.Count.EqualTo(4));
            Assert.That(harness.IsPublished(1, 104), Is.True);
            Assert.That(harness.IsPublished(4, 100), Is.True);
        }
    }

    [Test]
    public void Historical_recovery_reconstructs_entries_from_block_tree_and_receipts()
    {
        IndexTableStore store = new();
        // Historical blocks 0 to 19 are in the block tree; none has transactions, so no receipts are stored.
        BlockTree blockTree = BuildChain(Level2PublicationBlock);
        using Harness harness = new(store, blockTree, receiptFinder: new InMemoryReceiptStorage());

        harness.Commit(blockTree.FindBlock(Level2PublicationBlock, BlockTreeLookupOptions.None)!);

        using (Assert.EnterMultipleScope())
        {
            // Genesis block 0 has no parent block entry per EIP-8304 specification, so blocks 1-15 contribute 15 entries.
            Assert.That(store.Get(2, 0), Is.Not.Null.And.Count.EqualTo(15));
            for (long b = 0; b < 16; b++)
            {
                Assert.That(store.Get(0, b), Is.Not.Null);
            }
            Assert.That(harness.IsPublished(1, (long)Level2PublicationBlock), Is.True);
            Assert.That(harness.IsPublished(16, 0), Is.True);
        }
    }

    [Test]
    public void Historical_recovery_does_not_fall_back_to_canonical_block_when_branch_block_is_missing()
    {
        IndexTableStore store = new();
        BlockTree blockTree = BuildChain(3);

        // Only the canonical blocks at heights 1-3 have bodies; the branch blocks are known by header alone.
        BlockHeader parent = blockTree.Genesis!;
        for (ulong b = 1; b < 4; b++)
        {
            BlockHeader branchHeader = Build.A.BlockHeader.WithNumber(b).WithParent(parent).WithExtraData([1]).TestObject;
            blockTree.Insert(branchHeader, BlockTreeInsertHeaderOptions.NotOnMainChain | BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
            parent = branchHeader;
        }

        using Harness harness = new(store, blockTree, receiptFinder: new InMemoryReceiptStorage());

        // Block 4 publishes the level-1 table covering blocks 0-3 of its own branch.
        Assert.Throws<InvalidOperationException>(() => harness.Commit(Build.A.Block.WithNumber(4).WithParent(parent).TestObject));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(harness.IsPublished(1, 4), Is.True);
            Assert.That(harness.IsPublished(4, 0), Is.False);
        }
    }

    [Test]
    public void RollbackBlock_removes_uncommitted_block_entries()
    {
        IndexTableStore store = new();
        using Harness harness = new(store);

        Block block = BuildBlock(1);
        harness.Commit(block);

        Assert.That(store.Get(0, 1, block.Hash), Is.Not.Null);

        harness.Handler.RollbackBlock(block);

        Assert.That(store.Get(0, 1, block.Hash), Is.Null);
    }

    [Test]
    public void RollbackBlock_of_sibling_that_failed_before_committing_keeps_processed_block_tables()
    {
        IndexTableStore store = new();
        using Harness harness = new(store);

        Block processed = BuildBlock(1);
        harness.Commit(processed);
        harness.Handler.UpdateFinalBlockHash(processed);
        IReadOnlyList<IndexEntry>? table = store.Get(0, 1, processed.Hash);

        harness.Handler.RollbackBlock(Build.A.Block.WithNumber(1).WithParentHash(TestItem.KeccakB).TestObject);

        Assert.That(store.Get(0, 1, processed.Hash), Is.Not.Null.And.SameAs(table));
    }

    [Test]
    public void Branch_isolation_ensures_rejected_sibling_entries_are_not_used_in_canonical_chain()
    {
        IndexTableStore store = new();
        // Canonical blocks 0-7; block 8 publishes the level-1 table covering blocks 4-7.
        BlockTree blockTree = BuildChain(7);

        BlockHeader canonicalHeader4 = blockTree.FindHeader(4, BlockTreeLookupOptions.None)!;
        BlockHeader siblingHeader4 = Build.A.BlockHeader.WithNumber(4).WithParent(blockTree.FindHeader(3, BlockTreeLookupOptions.None)!).WithExtraData([1]).TestObject;
        Hash256 canonicalHash4 = canonicalHeader4.Hash!;
        Hash256 siblingHash4 = siblingHeader4.Hash!;

        IndexEntry canonicalEntry4 = IndexEntry.CreateTransaction(TestItem.KeccakC, 4, 0, 0);
        IndexEntry siblingEntry4 = IndexEntry.CreateTransaction(TestItem.KeccakD, 4, 0, 0);

        store.Store(0, 4, [canonicalEntry4], canonicalHash4);
        store.Store(0, 4, [siblingEntry4], siblingHash4);

        for (ulong b = 5; b <= 7; b++)
        {
            Hash256 h = blockTree.FindHeader(b, BlockTreeLookupOptions.None)!.Hash!;
            store.Store(0, (long)b, [IndexEntry.CreateBlock(h, b)], h);
        }

        Block block8 = Build.A.Block.WithNumber(8).WithParent(blockTree.Head!).TestObject;
        using Harness harness = new(store, blockTree, receiptFinder: new InMemoryReceiptStorage());

        harness.Commit(block8);

        IReadOnlyList<IndexEntry>? level1Table = store.Get(1, 4, block8.Hash);
        Assert.That(level1Table, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(level1Table, Does.Not.Contain(siblingEntry4));
            Assert.That(level1Table, Does.Contain(canonicalEntry4));
        }

        Block siblingBlock4 = new(siblingHeader4);
        IIndexTableHandler siblingHandler = harness.CreateHandler();
        harness.Commit(siblingBlock4, handler: siblingHandler);
        siblingHandler.RollbackBlock(siblingBlock4);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(0, 4, siblingHash4), Is.Null);
            Assert.That(store.Get(0, 4, canonicalHash4), Is.Not.Null);
        }
    }

    [Test]
    public void Recovered_tables_spanning_eip8141_activation_encode_frame_index_by_each_block_fork()
    {
        IndexTableStore store = new();
        InMemoryReceiptStorage receiptStorage = new();
        // Blocks 1, 4 and 7 carry transactions with one log each; block 8 publishes the level-1 table over blocks 4-7.
        BlockTree blockTree = Build.A.BlockTree()
            .WithTransactions(receiptStorage, static (_, _) => [Build.A.LogEntry.WithAddress(TestItem.AddressB).WithTopics().TestObject])
            .OfChainLength(8)
            .TestObject;

        IReleaseSpec framesSpec = BuildSpec(frames: true);
        CustomSpecProvider specProvider = new(((ForkActivation)0, BuildSpec()), ((ForkActivation)5, framesSpec));
        using Harness harness = new(store, blockTree, specProvider, receiptStorage);

        Block block8 = Build.A.Block.WithNumber(8).WithParent(blockTree.Head!).TestObject;
        harness.Commit(block8, framesSpec);

        Dictionary<ulong, int> logAddressLengths = [];
        foreach (IndexEntry entry in store.Get(1, 4, block8.Hash)!)
        {
            if (entry.Type == IndexEntryType.LogAddress)
            {
                logAddressLengths[entry.BlockNumber] = entry.EncodedLength;
            }
        }

        Assert.That(logAddressLengths, Is.EqualTo(new Dictionary<ulong, int> { [4] = 38, [7] = 40 }));
    }

    [Test]
    public void Genesis_block_commits_no_table()
    {
        IndexTableStore store = new();
        BlockTree blockTree = BuildChain(0);
        using Harness harness = new(store, blockTree);

        harness.Commit(blockTree.FindBlock(0, BlockTreeLookupOptions.None)!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(0, 0), Is.Null);
            Assert.That(harness.IsPublished(1, 0), Is.False);
        }
    }

    [Test]
    public void Missing_history_on_higher_level_table_throws_InvalidOperationException()
    {
        IndexTableStore store = new();
        BlockTree blockTree = BuildChain(Level2PublicationBlock, headersOnly: true);
        // Only store block 16 onward — blocks 0–15 are missing (post-sync scenario)
        StoreLevel0Tables(store, blockTree, 16, 4);
        using Harness harness = new(store, blockTree);

        // Block 19 triggers level-2 publication for blocks 0–15. When those cannot be built,
        // it must throw InvalidOperationException rather than silently skipping the state-mutating system call.
        Assert.Throws<InvalidOperationException>(() => harness.Commit(HeaderOnlyBlock(blockTree, Level2PublicationBlock)));
    }

    [Test]
    public void Higher_level_table_uses_cached_sub_tables_keyed_by_publication_block_hash()
    {
        IndexTableStore store = new();
        BlockTree blockTree = BuildChain(Level2PublicationBlock, headersOnly: true);

        // Store level 1 tables covering 0..3, 4..7, 8..11, 12..15 at their publication block hashes (blocks 4, 8, 12, 16)
        for (int i = 0; i < 4; i++)
        {
            long firstBlock = i * 4;
            long pubBlock = IndexTableMergeScheduler.PublicationBlock(1, firstBlock);
            List<IndexEntry> entries = [IndexEntry.CreateBlock(TestItem.Keccaks[i], (ulong)firstBlock)];
            store.Store(1, firstBlock, entries, blockTree.FindHeader((ulong)pubBlock, BlockTreeLookupOptions.None)!.Hash);
        }

        // Level-0 tables are absent from store, and without block bodies they cannot be recovered.
        using Harness harness = new(store, blockTree);

        // Block 19 publishes level-2 covering blocks 0–15.
        // It must read the 4 level-1 sub-tables from store (keyed by publication block hash)
        // rather than missing the cache and trying to rebuild from absent level-0 tables.
        Assert.DoesNotThrow(() => harness.Commit(HeaderOnlyBlock(blockTree, Level2PublicationBlock)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(2, 0), Is.Not.Null);
            Assert.That(harness.IsPublished(1, (long)Level2PublicationBlock), Is.True);
            Assert.That(harness.IsPublished(16, 0), Is.True);
        }
    }

    [Test]
    public void Enabled_without_contract_address_throws()
    {
        IndexTableStore store = new();
        using Harness harness = new(store);

        Assert.Throws<InvalidOperationException>(() => harness.Commit(BuildBlock(1), BuildSpec(withContractAddress: false)));
    }

    [Test]
    public void ExecuteSystemCall_skips_when_no_contract_code_at_address()
    {
        IndexTableStore store = new();
        using Harness harness = new(store, deployContract: false);

        harness.Commit(BuildBlock(1));

        using (Assert.EnterMultipleScope())
        {
            // Entries are still stored (computation is independent of contract deployment)
            Assert.That(store.Get(0, 1), Is.Not.Null);
            // The call would otherwise create an empty account at the contract address and change the state root.
            Assert.That(harness.ContractAccountExists, Is.False);
        }
    }

    [Test]
    public void RollbackBlock_ignores_blocks_committed_by_another_handler_instance()
    {
        IndexTableStore store = new();
        using Harness harness = new(store);

        Block blockA = BuildBlock(1);
        harness.Commit(blockA);
        Assert.That(store.Get(0, 1, blockA.Hash), Is.Not.Null);

        // Handler B never committed block 1, so rolling it back should be a no-op
        harness.CreateHandler().RollbackBlock(blockA);

        Assert.That(store.Get(0, 1, blockA.Hash), Is.Not.Null,
            "Rollback from a different handler instance must not remove entries it did not commit");
    }

    private static BlockTree BuildChain(ulong headNumber, bool headersOnly = false)
    {
        BlockTreeBuilder builder = Build.A.BlockTree();
        return (headersOnly ? builder.OfHeadersOnly : builder).OfChainLength((int)headNumber + 1).TestObject;
    }

    private static Block HeaderOnlyBlock(IBlockTree blockTree, ulong number) =>
        new(blockTree.FindHeader(number, BlockTreeLookupOptions.None)!);

    private static ISpecProvider BuildSpecProvider() => new TestSpecProvider(BuildSpec());

    private static IReleaseSpec BuildSpec(bool enabled = true, bool withContractAddress = true, bool frames = false) =>
        new OverridableReleaseSpec(Amsterdam.Instance)
        {
            IsEip8304Enabled = enabled,
            Eip8304ContractAddress = withContractAddress ? IndexContract : null,
            IsEip8141Enabled = frames,
        };

    private static Block BuildBlock(ulong number) =>
        Build.A.Block.WithNumber(number).WithParentHash(TestItem.KeccakA).TestObject;

    private static void StoreLevel0Tables(IIndexTableStore store, IBlockTree blockTree, long firstBlock, int count)
    {
        for (long block = firstBlock; block < firstBlock + count; block++)
        {
            List<IndexEntry> entries = [IndexEntry.CreateBlock(TestItem.Keccaks[(int)block], (ulong)block)];
            store.Store(0, block, entries, blockTree.FindHeader((ulong)block, BlockTreeLookupOptions.None)!.Hash);
        }
    }

    /// <summary>
    /// Production-wired handler over a fresh world state that holds the EIP-8304 index contract.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly IContainer _container;
        private readonly ILifetimeScope _scope;
        private readonly IDisposable _worldStateScope;
        private readonly IWorldState _worldState;
        private readonly ITransactionProcessor _processor;

        public Harness(
            IIndexTableStore store,
            IBlockTree? blockTree = null,
            ISpecProvider? specProvider = null,
            IReceiptFinder? receiptFinder = null,
            bool deployContract = true)
        {
            _container = new ContainerBuilder().AddModule(new TestNethermindModule(BuildSpec())).Build();
            _scope = _container.BeginLifetimeScope(builder =>
            {
                builder
                    .AddSingleton<IWorldStateScopeProvider>(_container.Resolve<IWorldStateManager>().GlobalWorldState)
                    .AddSingleton<IIndexTableStore>(store)
                    .AddSingleton<ISpecProvider>(specProvider ?? BuildSpecProvider());
                if (blockTree is not null) builder.AddSingleton<IBlockTree>(blockTree);
                if (receiptFinder is not null) builder.AddSingleton<IReceiptFinder>(receiptFinder);
            });

            _worldState = _scope.Resolve<IWorldState>();
            _worldStateScope = _worldState.BeginScope(IWorldState.PreGenesis);
            if (deployContract)
            {
                IReleaseSpec spec = BuildSpec();
                _worldState.CreateAccount(IndexContract, 0, 1);
                _worldState.InsertCode(IndexContract, IndexTableIntegrationTests.IndexContractCode, spec);
                _worldState.Commit(spec);
            }

            _processor = _scope.Resolve<ITransactionProcessor>();
            Handler = CreateHandler();
        }

        public IIndexTableHandler Handler { get; }

        public bool ContractAccountExists => _worldState.AccountExists(IndexContract);

        public IIndexTableHandler CreateHandler() => _scope.Resolve<IIndexTableHandlerFactory>().Create(_processor, _worldState);

        public void Commit(Block block, IReleaseSpec? spec = null, IIndexTableHandler? handler = null)
        {
            _processor.SetBlockExecutionContext(block.Header);
            (handler ?? Handler).CommitIndexTableRoots(block, [], spec ?? BuildSpec(), NullTxTracer.Instance);
        }

        public bool IsPublished(int tableSize, long firstBlock)
        {
            _worldState.Get(new StorageCell(IndexContract, IndexProofEngine.ComputeStorageSlot(tableSize, firstBlock)), out UInt256 root);
            return !root.IsZero;
        }

        public void Dispose()
        {
            _worldStateScope.Dispose();
            _scope.Dispose();
            _container.Dispose();
        }
    }
}
