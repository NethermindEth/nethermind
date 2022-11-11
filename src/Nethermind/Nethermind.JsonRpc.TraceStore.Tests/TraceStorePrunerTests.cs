//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
//
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
//
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
//

using System.Collections.Generic;
using System.Linq;
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
    public void prunes_old_blocks()
    {
        IEnumerable<Keccak> GenerateTraces(MemDb db, BlockTree tree)
        {
            ParityLikeBlockTracer parityTracer = new(ParityTraceTypes.Trace);
            DbPersistingBlockTracer<ParityLikeTxTrace, ParityLikeTxTracer> dbPersistingTracer =
                new(parityTracer, db, static t => TraceSerializer.Serialize(t), LimboLogs.Instance);

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
        keys.Skip(3).Select(k => memDb.Get(k)).Should().NotContain((byte[]?)null); // too old were not removed
        keys.Take(3).Select(k => memDb.Get(k)).Should().OnlyContain(b => b == null); // those were removed

    }
}
