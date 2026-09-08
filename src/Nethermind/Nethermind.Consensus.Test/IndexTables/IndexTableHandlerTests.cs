// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.IndexTables;
using Nethermind.Core;
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

        Assert.Multiple(() =>
        {
            Assert.That(store.Get(2, 0), Is.Not.Null.And.Count.EqualTo(16));
            // One call for the level-0 table of the current block, one for the level-2 table.
            processor.Received(2).Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        });
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

        Assert.Multiple(() =>
        {
            Assert.That(store.Get(0, (long)Level2PublicationBlock), Is.Null);
            processor.DidNotReceive().Execute(Arg.Any<Transaction>(), Arg.Any<ITxTracer>());
        });
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
