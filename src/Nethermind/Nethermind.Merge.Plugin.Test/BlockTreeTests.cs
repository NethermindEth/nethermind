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

using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Db.Blooms;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.State.Repositories;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class BlockTreeTests
{
    private (BlockTree notSyncedTree, BlockTree syncedTree) BuildBlockTrees(int notSyncedTreeSize, int syncedTreeSize)
    {
        BlockTreeBuilder treeBuilder = Build.A.BlockTree().OfChainLength(notSyncedTreeSize);
        BlockTree notSyncedTree = new(
            treeBuilder.BlocksDb,
            treeBuilder.HeadersDb,
            treeBuilder.BlockInfoDb,
            treeBuilder.MetadataDb,
            treeBuilder.ChainLevelInfoRepository,
            MainnetSpecProvider.Instance,
            NullBloomStorage.Instance,
            new SyncConfig(),
            LimboLogs.Instance);
        
        BlockTree syncedTree = new(
            treeBuilder.BlocksDb,
            treeBuilder.HeadersDb,
            treeBuilder.BlockInfoDb,
            treeBuilder.MetadataDb,
            treeBuilder.ChainLevelInfoRepository,
            MainnetSpecProvider.Instance,
            NullBloomStorage.Instance,
            new SyncConfig(),
            LimboLogs.Instance);

        Block parent = syncedTree.Head!;
        for (int i = 0; i < syncedTreeSize - notSyncedTreeSize; ++i)
        {
            Block block = Build.A.Block.WithNumber(parent!.Number + 1).WithParent(parent).TestObject;
            AddBlockResult addBlockResult = syncedTree.SuggestBlock(block);
            Assert.AreEqual(AddBlockResult.Added, addBlockResult);

            parent = block;
        }

        return (notSyncedTree, syncedTree);
    }
    
    [Test]
    public void Can_build_correct_block_tree()
    {
        BlockTreeBuilder treeBuilder = Build.A.BlockTree().OfChainLength(10);
        BlockTree tree = new(
            treeBuilder.BlocksDb,
            treeBuilder.HeadersDb,
            treeBuilder.BlockInfoDb,
            treeBuilder.MetadataDb,
            treeBuilder.ChainLevelInfoRepository,
            MainnetSpecProvider.Instance,
            NullBloomStorage.Instance,
            new SyncConfig(),
            LimboLogs.Instance);
        
        Assert.AreEqual(9, tree.BestKnownNumber);
        Assert.AreEqual(9, tree.BestSuggestedBody!.Number);
        Assert.AreEqual(9, tree.Head!.Number);
    }
    
    [Test]
    public void Can_start_insert_pivot_block()
    {
        (BlockTree notSyncedTree, BlockTree syncedTree) = BuildBlockTrees(10, 20);
        Block? beaconBlock = syncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        AddBlockResult insertResult = notSyncedTree.Insert(beaconBlock, true,
            BlockTreeInsertOptions.SkipUpdateBestPointers | BlockTreeInsertOptions.TotalDifficultyNotNeeded);
        
        Assert.AreEqual(AddBlockResult.Added, insertResult);
    }
}
