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
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
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
        StoreLevel0Tables(store, 0, 16);

        (IndexTableHandler handler, ITransactionProcessor processor) = BuildHandler(store);

        handler.CommitIndexTableRoots(BuildBlock(Level2PublicationBlock), [], BuildSpec(), NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(2, 0), Is.Not.Null.And.Count.EqualTo(16));
            // One call for the level-0 table of the current block, one for the level-2 table.
            processor.Received(2).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        }
    }

    [Test]
    public void Higher_level_table_that_cannot_be_built_fails_instead_of_skipping_the_system_call()
    {
        IndexTableStore store = new();
        // One block short of the range the level-2 table covers.
        StoreLevel0Tables(store, 1, 16);

        (IndexTableHandler handler, ITransactionProcessor processor) = BuildHandler(store);

        Assert.Throws<InvalidOperationException>(() =>
            handler.CommitIndexTableRoots(BuildBlock(Level2PublicationBlock), [], BuildSpec(), NullTxTracer.Instance));

        // The level-2 root must not be committed against an incomplete table.
        processor.Received(1).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
    }

    [Test]
    public void Nothing_is_committed_when_the_fork_is_not_active()
    {
        IndexTableStore store = new();
        (IndexTableHandler handler, ITransactionProcessor processor) = BuildHandler(store);

        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8304Enabled.Returns(false);

        handler.CommitIndexTableRoots(BuildBlock(Level2PublicationBlock), [], spec, NullTxTracer.Instance);

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
        ISpecProvider specProvider = Substitute.For<ISpecProvider>();

        // Fork activates at block 100
        specProvider.GetSpec(Arg.Any<ForkActivation>()).Returns(callInfo =>
        {
            ForkActivation fa = callInfo.ArgAt<ForkActivation>(0);
            IReleaseSpec spec = Substitute.For<IReleaseSpec>();
            spec.IsEip8304Enabled.Returns(fa.BlockNumber >= 100);
            spec.Eip8304ContractAddress.Returns(TestItem.AddressA);
            return spec;
        });

        ITransactionProcessor processor = Substitute.For<ITransactionProcessor>();
        IndexTableHandler handler = new(processor, store, specProvider);

        IReleaseSpec activeSpec = Substitute.For<IReleaseSpec>();
        activeSpec.IsEip8304Enabled.Returns(true);
        activeSpec.Eip8304ContractAddress.Returns(TestItem.AddressA);

        // Block 100: candidate level-1 table covers 96-99 (firstBlock = 96), which was pre-activation
        Block block100 = BuildBlock(100);
        handler.CommitIndexTableRoots(block100, [], activeSpec, NullTxTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(0, 100), Is.Not.Null);
            Assert.That(store.Get(1, 96), Is.Null);
            processor.Received(1).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        }

        // Store level-0 tables for post-activation blocks 100-103
        for (long b = 100; b <= 103; b++)
        {
            store.Store(0, b, [IndexEntry.CreateBlock(TestItem.Keccaks[(int)b], (ulong)b)]);
        }

        // Block 104 publishes level-1 covering 100-103
        Block block104 = BuildBlock(104);
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
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        IReceiptStorage receiptStorage = Substitute.For<IReceiptStorage>();

        // Historical blocks 0 to 15 are in blockTree and receipts in receiptStorage
        for (ulong b = 0; b < 16; b++)
        {
            Block histBlock = BuildBlock(b);
            blockTree.FindBlock(b, BlockTreeLookupOptions.None).Returns(histBlock);
            blockTree.FindBlock(histBlock.Hash!, BlockTreeLookupOptions.None, b).Returns(histBlock);
            receiptStorage.Get(histBlock).Returns([]);
        }

        ITransactionProcessor processor = Substitute.For<ITransactionProcessor>();
        IndexTableHandler handler = new(processor, store, blockTree: blockTree, receiptStorage: receiptStorage);

        handler.CommitIndexTableRoots(BuildBlock(Level2PublicationBlock), [], BuildSpec(), NullTxTracer.Instance);

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
        IBlockTree blockTree = Substitute.For<IBlockTree>();

        Hash256 canonicalHash0 = TestItem.KeccakA;
        Hash256 siblingHash0 = TestItem.KeccakB;

        BlockHeader canonicalHeader0 = Build.A.BlockHeader.WithNumber(0).WithHash(canonicalHash0).TestObject;
        BlockHeader siblingHeader0 = Build.A.BlockHeader.WithNumber(0).WithHash(siblingHash0).TestObject;

        IndexEntry canonicalEntry0 = IndexEntry.CreateTransaction(TestItem.KeccakC, 0, 0, 0);
        IndexEntry siblingEntry0 = IndexEntry.CreateTransaction(TestItem.KeccakD, 0, 0, 0);

        store.Store(0, 0, [canonicalEntry0], canonicalHash0);
        store.Store(0, 0, [siblingEntry0], siblingHash0);

        Hash256 prevHash = canonicalHash0;
        for (ulong b = 1; b <= 3; b++)
        {
            Hash256 h = TestItem.Keccaks[(int)b];
            BlockHeader header = Build.A.BlockHeader.WithNumber(b).WithHash(h).WithParentHash(prevHash).TestObject;
            blockTree.FindHeader(h, BlockTreeLookupOptions.None).Returns(header);
            store.Store(0, (long)b, [IndexEntry.CreateBlock(h, b)], h);
            prevHash = h;
        }
        blockTree.FindHeader(canonicalHash0, BlockTreeLookupOptions.None).Returns(canonicalHeader0);

        Block block4 = Build.A.Block.WithNumber(4).WithParentHash(prevHash).TestObject;

        ITransactionProcessor processor = Substitute.For<ITransactionProcessor>();
        IndexTableHandler handler = new(processor, store, blockTree: blockTree);

        handler.CommitIndexTableRoots(block4, [], BuildSpec(), NullTxTracer.Instance);

        IReadOnlyList<IndexEntry>? level1Table = store.Get(1, 0, block4.Hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(level1Table, Is.Not.Null);
            bool containsSibling = false;
            bool containsCanonical = false;
            foreach (IndexEntry entry in level1Table!)
            {
                if (entry.CompareTo(siblingEntry0) == 0)
                {
                    containsSibling = true;
                }
                if (entry.CompareTo(canonicalEntry0) == 0)
                {
                    containsCanonical = true;
                }
            }
            Assert.That(containsSibling, Is.False);
            Assert.That(containsCanonical, Is.True);
        }

        siblingHeader0.Hash = siblingHash0;
        Block siblingBlock0 = new(siblingHeader0);
        handler.RollbackBlock(siblingBlock0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(store.Get(0, 0, siblingHash0), Is.Null);
            Assert.That(store.Get(0, 0, canonicalHash0), Is.Not.Null);
        }
    }

    private static (IndexTableHandler, ITransactionProcessor) BuildHandler(IIndexTableStore store)
    {
        ITransactionProcessor processor = Substitute.For<ITransactionProcessor>();
        return (new IndexTableHandler(processor, store), processor);
    }

    private static IReleaseSpec BuildSpec()
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8304Enabled.Returns(true);
        spec.Eip8304ContractAddress.Returns(TestItem.AddressA);
        return spec;
    }

    private static Block BuildBlock(ulong number) =>
        Build.A.Block.WithNumber(number).WithParentHash(TestItem.KeccakA).TestObject;

    private static void StoreLevel0Tables(IIndexTableStore store, long firstBlock, int count)
    {
        for (long block = firstBlock; block < firstBlock + count; block++)
        {
            List<IndexEntry> entries = [IndexEntry.CreateBlock(TestItem.Keccaks[(int)block], (ulong)block)];
            store.Store(0, block, entries);
        }
    }
}
