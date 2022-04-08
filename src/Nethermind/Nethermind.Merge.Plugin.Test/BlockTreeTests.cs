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
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public class BlockTreeTests
{
    private BlockTreeInsertOptions GetBlockTreeInsertOptions()
    {
        return BlockTreeInsertOptions.TotalDifficultyNotNeeded
               | BlockTreeInsertOptions.SkipUpdateBestPointers 
               | BlockTreeInsertOptions.UpdateBeaconPointers;
    }
    
    private (BlockTree notSyncedTree, BlockTree syncedTree) BuildBlockTrees(
        int notSyncedTreeSize, int syncedTreeSize, IDb? metadataDb = null)
    {
        BlockTreeBuilder treeBuilder = Build.A.BlockTree().OfChainLength(notSyncedTreeSize);
        BlockTree notSyncedTree = new(
            treeBuilder.BlocksDb,
            treeBuilder.HeadersDb,
            treeBuilder.BlockInfoDb,
            metadataDb ?? treeBuilder.MetadataDb,
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
    public void Can_start_insert_pivot_block_with_correct_pointers()
    {
        (BlockTree notSyncedTree, BlockTree syncedTree) = BuildBlockTrees(10, 20);
        Block? beaconBlock = syncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        BlockTreeInsertOptions insertOption = GetBlockTreeInsertOptions();
        AddBlockResult insertResult = notSyncedTree.Insert(beaconBlock, true, insertOption);
        
        Assert.AreEqual(AddBlockResult.Added, insertResult);
        Assert.AreEqual(9, notSyncedTree.BestKnownNumber);
        Assert.AreEqual(9, notSyncedTree.BestSuggestedHeader!.Number);
        Assert.AreEqual(9, notSyncedTree.Head!.Number);
        Assert.AreEqual(9, notSyncedTree.BestSuggestedBody!.Number);
        Assert.AreEqual(14, notSyncedTree.BestKnownBeaconNumber);
        Assert.AreEqual(14, notSyncedTree.BestSuggestedBeaconHeader!.Number);
    }
    
        
    [Test]
    public void Can_insert_beacon_headers()
    {
        (BlockTree notSyncedTree, BlockTree syncedTree) = BuildBlockTrees(10, 20);
        
        Block? beaconBlock = syncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        BlockTreeInsertOptions options = GetBlockTreeInsertOptions();
        AddBlockResult insertResult = notSyncedTree.Insert(beaconBlock, true, options);
        for (int i = 13; i > 9; --i)
        {
            BlockHeader? beaconHeader = syncedTree.FindHeader(i, BlockTreeLookupOptions.None);
            AddBlockResult insertOutcome = notSyncedTree.Insert(beaconHeader!, options);
            Assert.AreEqual(insertOutcome, insertResult);
        }
    }
    
    [Test]
    public void Can_fill_beacon_headers_gap()
    {
        (BlockTree notSyncedTree, BlockTree syncedTree) = BuildBlockTrees(10, 20);
        
        Block? beaconBlock = syncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        BlockTreeInsertOptions options = GetBlockTreeInsertOptions();
        AddBlockResult insertResult = notSyncedTree.Insert(beaconBlock, true, options);
        
        for (int i = 13; i > 9; --i)
        {
            BlockHeader? beaconHeader = syncedTree.FindHeader(i, BlockTreeLookupOptions.None);
            AddBlockResult insertOutcome = notSyncedTree.Insert(beaconHeader!, options);
            Assert.AreEqual(AddBlockResult.Added, insertOutcome);
        }
        
        for (int i = 10; i <14; ++i)
        {
            Block? block = syncedTree.FindBlock(i, BlockTreeLookupOptions.None);
            AddBlockResult insertOutcome = notSyncedTree.SuggestBlock(block!);
            Assert.AreEqual(AddBlockResult.Added, insertOutcome);
        }
        
        Assert.AreEqual(13, notSyncedTree.BestSuggestedBody!.Number);
    }

    [Test]
    public void Can_load_and_set_lowest_beacon_headers_in_metadata_db()
    {
        IDb metadataDb = new MemDb();
        BlockTreeInsertOptions options = GetBlockTreeInsertOptions();
        (BlockTree notSyncedTree, BlockTree syncedTree) = BuildBlockTrees(10, 20, metadataDb);
        Block? beaconBlock = syncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        notSyncedTree.Insert(beaconBlock!, true, options);

        BlockHeader? beaconHeader = null;
        for (int i = 13; i > 11; --i)
        {
            beaconHeader = syncedTree.FindHeader(i, BlockTreeLookupOptions.None);
            AddBlockResult insertOutcome = notSyncedTree.Insert(beaconHeader!, options);
            Assert.AreEqual(AddBlockResult.Added, insertOutcome);
        }
        
        Assert.AreEqual(beaconHeader, notSyncedTree.LowestInsertedBeaconHeader);
        Assert.AreEqual(beaconHeader?.Hash, metadataDb.Get(MetadataDbKeys.LowestInsertedBeaconHeaderHash).AsRlpStream().DecodeKeccak());
        
        BlockTreeBuilder treeBuilder = Build.A.BlockTree().OfChainLength(20);
        BlockTree tree = new(
            treeBuilder.BlocksDb,
            treeBuilder.HeadersDb,
            treeBuilder.BlockInfoDb,
            metadataDb,
            treeBuilder.ChainLevelInfoRepository,
            MainnetSpecProvider.Instance,
            NullBloomStorage.Instance,
            new SyncConfig(),
            LimboLogs.Instance);
        
        Assert.AreEqual(tree.LowestInsertedBeaconHeader?.Hash, beaconHeader?.Hash);
        Assert.AreEqual(metadataDb.Get(MetadataDbKeys.LowestInsertedBeaconHeaderHash).AsRlpStream().DecodeKeccak(), beaconHeader?.Hash);
    }
}
