// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.IndexTables;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.IndexTables;

public class IndexTableHandlerTests
{
    /// <summary>
    /// A level-2 table spans 16 blocks and is published <c>16 / 4</c> blocks after the last one,
    /// i.e. block 19 publishes the table covering blocks 0-15.
    /// </summary>
    private const ulong Level2PublicationBlock = 19;

    [Test]
    public void Higher_level_table_is_rebuilt_from_the_level_below_when_sub_tables_are_missing()
    {
        IndexTableStore store = new();
        BlockTree blockTree = BuildChain(Level2PublicationBlock);
        StoreLevel0Tables(store, blockTree, 0, 16);

        (IndexTableHandler handler, ITransactionProcessor processor) = BuildHandler(store, blockTree);

        handler.CommitIndexTableRoots(blockTree.FindBlock(Level2PublicationBlock, BlockTreeLookupOptions.None)!, [], BuildSpec(), NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(2, 0), Is.Not.Null.And.Count.EqualTo(16));
            // One call for the level-0 table of the current block, one for the level-2 table.
            processor.Received(2).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        }
    }

    [Test]
    public void Higher_level_table_that_cannot_be_built_throws_InvalidOperationException()
    {
        IndexTableStore store = new();
        BlockTree blockTree = BuildChain(Level2PublicationBlock);
        // One block short of the range the level-2 table covers.
        StoreLevel0Tables(store, blockTree, 1, 16);

        (IndexTableHandler handler, ITransactionProcessor processor) = BuildHandler(store, blockTree);

        Assert.Throws<InvalidOperationException>(() =>
            handler.CommitIndexTableRoots(blockTree.FindBlock(Level2PublicationBlock, BlockTreeLookupOptions.None)!, [], BuildSpec(), NullTxTracer.Instance));

        // The level-2 root must not be committed against an incomplete table.
        processor.Received(1).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
    }

    [Test]
    public void Nothing_is_committed_when_the_fork_is_not_active()
    {
        IndexTableStore store = new();
        (IndexTableHandler handler, ITransactionProcessor processor) = BuildHandler(store);

        handler.CommitIndexTableRoots(BuildBlock(Level2PublicationBlock), [], BuildSpec(enabled: false), NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(0, (long)Level2PublicationBlock), Is.Null);
            processor.DidNotReceive().Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        }
    }

    [Test]
    public void Activation_boundary_transition_skips_pre_activation_tables()
    {
        IndexTableStore store = new();
        IReleaseSpec activeSpec = BuildSpec();
        // Fork activates at block 100
        CustomSpecProvider specProvider = new(((ForkActivation)0, BuildSpec(enabled: false)), ((ForkActivation)100, activeSpec));

        ITransactionProcessor processor = Substitute.For<ITransactionProcessor>();
        BlockTree blockTree = BuildChain(104);
        IndexTableHandler handler = new(processor, store, specProvider, blockTree: blockTree);

        // Block 100: candidate level-1 table covers 96-99 (firstBlock = 96), which was pre-activation
        Block block100 = blockTree.FindBlock(100, BlockTreeLookupOptions.None)!;
        handler.CommitIndexTableRoots(block100, [], activeSpec, NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(0, 100), Is.Not.Null);
            Assert.That(store.Get(1, 96), Is.Null);
            processor.Received(1).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        }

        // Store level-0 tables for post-activation blocks 100-103
        StoreLevel0Tables(store, blockTree, 100, 4);

        // Block 104 publishes level-1 covering 100-103
        Block block104 = blockTree.FindBlock(104, BlockTreeLookupOptions.None)!;
        handler.CommitIndexTableRoots(block104, [], activeSpec, NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(1, 100), Is.Not.Null.And.Count.EqualTo(4));
            processor.Received(3).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        }
    }

    [Test]
    public void Historical_recovery_reconstructs_entries_from_block_tree_and_receipts()
    {
        IndexTableStore store = new();
        // Historical blocks 0 to 19 are in the block tree; none has transactions, so no receipts are stored.
        BlockTree blockTree = Build.A.BlockTree().OfChainLength((int)Level2PublicationBlock + 1).TestObject;

        ITransactionProcessor processor = Substitute.For<ITransactionProcessor>();
        IndexTableHandler handler = new(processor, store, BuildSpecProvider(), blockTree: blockTree, receiptStorage: new InMemoryReceiptStorage());

        handler.CommitIndexTableRoots(blockTree.FindBlock(Level2PublicationBlock, BlockTreeLookupOptions.None)!, [], BuildSpec(), NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            // Genesis block 0 has no parent block entry per EIP-8304 specification, so blocks 1-15 contribute 15 entries.
            Assert.That(store.Get(2, 0), Is.Not.Null.And.Count.EqualTo(15));
            for (long b = 0; b < 16; b++)
            {
                Assert.That(store.Get(0, b), Is.Not.Null);
            }
            processor.Received(2).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        }
    }

    [Test]
    public void Historical_recovery_does_not_fall_back_to_canonical_block_when_branch_block_is_missing()
    {
        IndexTableStore store = new();
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(4).TestObject;

        // Only the canonical blocks at heights 1-3 have bodies; the branch blocks are known by header alone.
        BlockHeader parent = blockTree.Genesis!;
        for (ulong b = 1; b < 4; b++)
        {
            BlockHeader branchHeader = Build.A.BlockHeader.WithNumber(b).WithParent(parent).WithExtraData([1]).TestObject;
            blockTree.Insert(branchHeader, BlockTreeInsertHeaderOptions.NotOnMainChain | BlockTreeInsertHeaderOptions.TotalDifficultyNotNeeded);
            parent = branchHeader;
        }

        ITransactionProcessor processor = Substitute.For<ITransactionProcessor>();
        IndexTableHandler handler = new(processor, store, BuildSpecProvider(), blockTree: blockTree, receiptStorage: new InMemoryReceiptStorage());

        // Block 4 publishes the level-1 table covering blocks 0-3 of its own branch.
        Block block4 = Build.A.Block.WithNumber(4).WithParent(parent).TestObject;

        Assert.Throws<InvalidOperationException>(() =>
            handler.CommitIndexTableRoots(block4, [], BuildSpec(), NullTxTracer.Instance));
        processor.Received(1).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
    }

    [Test]
    public void RollbackBlock_removes_uncommitted_block_entries()
    {
        IndexTableStore store = new();
        (IndexTableHandler handler, _) = BuildHandler(store);

        Block block = BuildBlock(1);
        handler.CommitIndexTableRoots(block, [], BuildSpec(), NullTxTracer.Instance);

        Assert.That(store.Get(0, 1, block.Hash), Is.Not.Null);

        handler.RollbackBlock(block);

        Assert.That(store.Get(0, 1, block.Hash), Is.Null);
    }

    [Test]
    public void Branch_isolation_ensures_rejected_sibling_entries_are_not_used_in_canonical_chain()
    {
        IndexTableStore store = new();
        // Canonical blocks 0-7; block 8 publishes the level-1 table covering blocks 4-7.
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(8).TestObject;
        IReceiptStorage receiptStorage = new InMemoryReceiptStorage();

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

        ITransactionProcessor processor = Substitute.For<ITransactionProcessor>();
        ISpecProvider specProvider = BuildSpecProvider();
        IndexTableHandler handler = new(processor, store, specProvider, blockTree: blockTree, receiptStorage: receiptStorage);

        handler.CommitIndexTableRoots(block8, [], BuildSpec(), NullTxTracer.Instance);

        IReadOnlyList<IndexEntry>? level1Table = store.Get(1, 4, block8.Hash);
        Assert.That(level1Table, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            bool containsSibling = false;
            bool containsCanonical = false;
            foreach (IndexEntry entry in level1Table!)
            {
                if (entry.CompareTo(siblingEntry4) == 0)
                {
                    containsSibling = true;
                }
                if (entry.CompareTo(canonicalEntry4) == 0)
                {
                    containsCanonical = true;
                }
            }
            Assert.That(containsSibling, Is.False);
            Assert.That(containsCanonical, Is.True);
        }

        Block siblingBlock4 = new(siblingHeader4);
        IndexTableHandler siblingHandler = new(processor, store, specProvider, blockTree: blockTree, receiptStorage: receiptStorage);
        siblingHandler.CommitIndexTableRoots(siblingBlock4, [], BuildSpec(), NullTxTracer.Instance);
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
        IndexTableHandler handler = new(Substitute.For<ITransactionProcessor>(), store, specProvider, blockTree: blockTree, receiptStorage: receiptStorage);

        Block block8 = Build.A.Block.WithNumber(8).WithParent(blockTree.Head!).TestObject;
        handler.CommitIndexTableRoots(block8, [], framesSpec, NullTxTracer.Instance);

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

    private static (IndexTableHandler, ITransactionProcessor) BuildHandler(IIndexTableStore store, IBlockTree? blockTree = null)
    {
        ITransactionProcessor processor = Substitute.For<ITransactionProcessor>();
        return (new IndexTableHandler(processor, store, BuildSpecProvider(), blockTree: blockTree), processor);
    }

    private static BlockTree BuildChain(ulong headNumber) => Build.A.BlockTree().OfChainLength((int)headNumber + 1).TestObject;

    private static ISpecProvider BuildSpecProvider() => new TestSpecProvider(BuildSpec());

    private static IReleaseSpec BuildSpec(bool enabled = true, bool withContractAddress = true, bool frames = false) =>
        new OverridableReleaseSpec(Amsterdam.Instance)
        {
            IsEip8304Enabled = enabled,
            Eip8304ContractAddress = withContractAddress ? TestItem.AddressA : null,
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

    [Test]
    public void Missing_history_on_higher_level_table_throws_InvalidOperationException()
    {
        IndexTableStore store = new();
        BlockTree blockTree = BuildChain(Level2PublicationBlock);
        // Only store block 16 onward — blocks 0–15 are missing (post-sync scenario)
        StoreLevel0Tables(store, blockTree, 16, 4);

        (IndexTableHandler handler, _) = BuildHandler(store, blockTree);

        // Block 19 triggers level-2 publication for blocks 0–15. When those cannot be built,
        // it must throw InvalidOperationException rather than silently skipping the state-mutating system call.
        Assert.Throws<InvalidOperationException>(() =>
            handler.CommitIndexTableRoots(blockTree.FindBlock(Level2PublicationBlock, BlockTreeLookupOptions.None)!, [], BuildSpec(), NullTxTracer.Instance));
    }

    [Test]
    public void Higher_level_table_uses_cached_sub_tables_keyed_by_publication_block_hash()
    {
        IndexTableStore store = new();
        // Header chain from block 0 to 19 so FindAncestorHeader resolves publication blocks
        BlockTree blockTree = Build.A.BlockTree().OfChainLength((int)Level2PublicationBlock + 1).TestObject;

        // Store level 1 tables covering 0..3, 4..7, 8..11, 12..15 at their publication block hashes (blocks 4, 8, 12, 16)
        for (int i = 0; i < 4; i++)
        {
            long firstBlock = i * 4;
            long pubBlock = IndexTableMergeScheduler.PublicationBlock(1, firstBlock);
            List<IndexEntry> entries = [IndexEntry.CreateBlock(TestItem.Keccaks[i], (ulong)firstBlock)];
            store.Store(1, firstBlock, entries, blockTree.FindHeader((ulong)pubBlock, BlockTreeLookupOptions.None)!.Hash);
        }

        // Level-0 tables are absent from store, and without receipt storage they cannot be recovered.
        ITransactionProcessor processor = Substitute.For<ITransactionProcessor>();
        IndexTableHandler handler = new(processor, store, BuildSpecProvider(), blockTree: blockTree);

        Block block19 = blockTree.FindBlock(Level2PublicationBlock, BlockTreeLookupOptions.None)!;

        // Block 19 publishes level-2 covering blocks 0–15.
        // It must read the 4 level-1 sub-tables from store (keyed by publication block hash)
        // rather than missing the cache and trying to rebuild from absent level-0 tables.
        Assert.DoesNotThrow(() =>
            handler.CommitIndexTableRoots(block19, [], BuildSpec(), NullTxTracer.Instance));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(2, 0), Is.Not.Null);
            processor.Received(2).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        }
    }

    [Test]
    public void Enabled_without_contract_address_throws()
    {
        IndexTableStore store = new();
        (IndexTableHandler handler, _) = BuildHandler(store);

        Assert.Throws<InvalidOperationException>(() =>
            handler.CommitIndexTableRoots(BuildBlock(1), [], BuildSpec(withContractAddress: false), NullTxTracer.Instance));
    }

    [Test]
    public void ExecuteSystemCall_skips_when_no_contract_code_at_address()
    {
        IndexTableStore store = new();
        ITransactionProcessor processor = Substitute.For<ITransactionProcessor>();
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = worldState.BeginScope(IWorldState.PreGenesis);

        IndexTableHandler handler = new(processor, store, BuildSpecProvider(), worldState);

        handler.CommitIndexTableRoots(BuildBlock(1), [], BuildSpec(), NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            // Entries are still stored (computation is independent of contract deployment)
            Assert.That(store.Get(0, 1), Is.Not.Null);
            // But no system call is executed because the contract has no code
            processor.DidNotReceive().Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        }
    }

    [Test]
    public void RollbackBlock_ignores_blocks_committed_by_another_handler_instance()
    {
        IndexTableStore store = new();

        (IndexTableHandler handlerA, _) = BuildHandler(store);
        (IndexTableHandler handlerB, _) = BuildHandler(store);

        Block blockA = BuildBlock(1);
        handlerA.CommitIndexTableRoots(blockA, [], BuildSpec(), NullTxTracer.Instance);
        Assert.That(store.Get(0, 1, blockA.Hash), Is.Not.Null);

        // Handler B never committed block 1, so rolling it back should be a no-op
        handlerB.RollbackBlock(blockA);

        Assert.That(store.Get(0, 1, blockA.Hash), Is.Not.Null,
            "Rollback from a different handler instance must not remove entries it did not commit");
    }
}
