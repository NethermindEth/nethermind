// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.Logging;
using NUnit.Framework;

namespace Nethermind.JsonRpc.TraceStore.Tests;

[Parallelizable(ParallelScope.All)]
public class TraceStorePrunerTests
{
    [Test]
    public async Task prunes_old_blocks()
    {
        IEnumerable<Keccak> GenerateTraces(MemDb db, BlockTree tree)
        {
            ParityLikeBlockTracer parityTracer = new(ParityTraceTypes.Trace);
            ParityLikeTraceSerializer serializer = new(LimboLogs.Instance);
            DbPersistingBlockTracer<ParityLikeTxTrace, ParityLikeTxTracer> dbPersistingTracer =
                new(parityTracer, db, serializer, LimboLogs.Instance);

            Block? current = tree.Head;
            while (current is not null)
            {
                dbPersistingTracer.StartNewBlockTrace(current);
                dbPersistingTracer.StartNewTxTrace(Build.A.Transaction.TestObject);
                dbPersistingTracer.EndTxTrace();
                dbPersistingTracer.EndBlockTrace();
                yield return current.Hash!;
                current = tree.FindParent(current, BlockTreeLookupOptions.None);
            }
        }

        void AddNewBlocks(BlockTree tree)
        {
            Block headPlus1 = Build.A.Block.WithParent(tree.Head!).TestObject;
            Block headPlus2 = Build.A.Block.WithParent(headPlus1).TestObject;
            Block headPlus3 = Build.A.Block.WithParent(headPlus2).TestObject;
            tree.SuggestBlock(headPlus1);
            tree.SuggestBlock(headPlus2);
            tree.SuggestBlock(headPlus3);
            Block[] blocks = { headPlus1, headPlus2, headPlus3 };
            tree.UpdateMainChain(blocks, true);
        }

        MemDb memDb = new();
        BlockTree blockTree = Build.A.BlockTree().OfChainLength(5).TestObject;
        TraceStorePruner tracePruner = new(blockTree, memDb, 3, LimboLogs.Instance);
        List<Keccak> keys = GenerateTraces(memDb, blockTree).ToList();
        keys.Select(k => memDb.Get(k)).Should().NotContain((byte[]?)null);
        AddNewBlocks(blockTree);
        await Task.Delay(100);
        keys.Skip(3).Select(k => memDb.Get(k)).Should().NotContain((byte[]?)null); // too old were not removed
        keys.Take(3).Select(k => memDb.Get(k)).Should().OnlyContain(b => b == null); // those were removed

    }
}
