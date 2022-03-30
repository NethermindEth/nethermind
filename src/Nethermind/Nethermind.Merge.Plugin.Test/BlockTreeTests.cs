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
    private (BlockTree notSyncedTree, BlockTree syncedTree)  BuildBlockTrees(int notSyncedTreeSize, int syncedTreeSize)
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
        Assert.AreEqual(9, notSyncedTree.BestKnownNumber);
    }
    
    [Test]
    public void Can_insert_beacon_headers()
    {
        (BlockTree notSyncedTree, BlockTree syncedTree) = BuildBlockTrees(10, 20);
        
        Block? beaconBlock = syncedTree.FindBlock(14, BlockTreeLookupOptions.None);
        AddBlockResult insertResult = notSyncedTree.Insert(beaconBlock, true,
            BlockTreeInsertOptions.SkipUpdateBestPointers | BlockTreeInsertOptions.TotalDifficultyNotNeeded);

        BlockTreeInsertOptions options = BlockTreeInsertOptions.TotalDifficultyNotNeeded | BlockTreeInsertOptions.SkipUpdateBestPointers;
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
        AddBlockResult insertResult = notSyncedTree.Insert(beaconBlock, true,
            BlockTreeInsertOptions.SkipUpdateBestPointers | BlockTreeInsertOptions.TotalDifficultyNotNeeded);

        BlockTreeInsertOptions options = BlockTreeInsertOptions.TotalDifficultyNotNeeded | BlockTreeInsertOptions.SkipUpdateBestPointers;
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

    public static class BlockTreeTestScenario
        {
            public class ScenarioBuilder
            {
                private BlockTreeBuilder? _syncedTreeBuilder;
                private BlockTree? _syncedTree;
                private BlockTreeBuilder? _notSyncedTreeBuilder;
                private BlockTree? _notSyncedTree;

                public ScenarioBuilder WithBlockTrees(int notSyncedTreeSize, int syncedTreeSize = -1)
                {
                    _notSyncedTreeBuilder = Build.A.BlockTree().OfChainLength(notSyncedTreeSize);
                    _notSyncedTree = new(
                        _notSyncedTreeBuilder.BlocksDb,
                        _notSyncedTreeBuilder.HeadersDb,
                        _notSyncedTreeBuilder.BlockInfoDb,
                        _notSyncedTreeBuilder.MetadataDb,
                        _notSyncedTreeBuilder.ChainLevelInfoRepository,
                        MainnetSpecProvider.Instance,
                        NullBloomStorage.Instance,
                        new SyncConfig(),
                        LimboLogs.Instance);

                    if (syncedTreeSize > 0)
                    {
                        _syncedTreeBuilder = Build.A.BlockTree().OfChainLength(syncedTreeSize);
                        _syncedTree = new(
                            _syncedTreeBuilder.BlocksDb,
                            _syncedTreeBuilder.HeadersDb,
                            _syncedTreeBuilder.BlockInfoDb,
                            _syncedTreeBuilder.MetadataDb,
                            _syncedTreeBuilder.ChainLevelInfoRepository,
                            MainnetSpecProvider.Instance,
                            NullBloomStorage.Instance,
                            new SyncConfig(),
                            LimboLogs.Instance);
                    }

                    return this;
                }

                public ScenarioBuilder InsertBeaconPivot(long num)
                {
                    Block? beaconBlock = _syncedTree!.FindBlock(num, BlockTreeLookupOptions.None);
                    AddBlockResult insertResult = _notSyncedTree!.Insert(beaconBlock!, true,
                        BlockTreeInsertOptions.SkipUpdateBestPointers | BlockTreeInsertOptions.TotalDifficultyNotNeeded);
                    Assert.AreEqual(AddBlockResult.Added, insertResult);
                    return this;
                }

                public ScenarioBuilder SuggestBlocks(long low, long high)
                {
                    BlockTreeInsertOptions options = BlockTreeInsertOptions.TotalDifficultyNotNeeded | BlockTreeInsertOptions.SkipUpdateBestPointers;
                    for (long i = low; i >= high; i++)
                    {
                        Block? beaconBlock = _syncedTree!.FindBlock(i, BlockTreeLookupOptions.None);
                        AddBlockResult insertResult = _notSyncedTree!.Insert(beaconBlock!, true, options);
                        Assert.AreEqual(AddBlockResult.Added, insertResult);
                    }
                    return this;
                }

                public ScenarioBuilder InsertHeaders(long low, long high)
                {
                    BlockTreeInsertOptions options = BlockTreeInsertOptions.TotalDifficultyNotNeeded | BlockTreeInsertOptions.SkipUpdateBestPointers;;
                    for (long i = high; i >= low; --i)
                    {
                        BlockHeader? beaconHeader = _syncedTree!.FindHeader(i, BlockTreeLookupOptions.None);
                        AddBlockResult insertResult = _notSyncedTree!.Insert(beaconHeader!, options);
                        Assert.AreEqual(AddBlockResult.Added, insertResult);
                    }
                    return this;
                }

                public ScenarioBuilder Restart()
                {
                    _notSyncedTree = new(
                        _notSyncedTreeBuilder!.BlocksDb,
                        _notSyncedTreeBuilder.HeadersDb,
                        _notSyncedTreeBuilder.BlockInfoDb,
                        _notSyncedTreeBuilder.MetadataDb,
                        _notSyncedTreeBuilder.ChainLevelInfoRepository,
                        MainnetSpecProvider.Instance,
                        NullBloomStorage.Instance,
                        new SyncConfig(),
                        LimboLogs.Instance);
                    return this;
                }

                public ScenarioBuilder AssertBestKnownNumber(long expected)
                {
                    Assert.AreEqual(expected,_notSyncedTree!.BestKnownNumber);
                    // Console.WriteLine("BestKnownNumber:"+_notSyncedTree!.BestKnownNumber);
                    return this;
                }

                public ScenarioBuilder AssertBestSuggestedHeader(long expected)
                {
                    Assert.AreEqual(expected,_notSyncedTree!.BestSuggestedHeader!.Number);
                    // Console.WriteLine("BestSuggestedHeader:"+_notSyncedTree!.BestSuggestedHeader!.Number);
                    return this;
                }

                public ScenarioBuilder AssertBestSuggestedBody(long expected)
                {
                    Assert.AreEqual(expected,_notSyncedTree!.BestSuggestedBody!.Number);
                    // Console.WriteLine("BestSuggestedBody:"+_notSyncedTree!.BestSuggestedBody!.Number);
                    return this;
                }
                
            }

            public static ScenarioBuilder GoesLikeThis()
            {
                return new();
            }
        }
        
    [Test]
    public void Best_pointers_are_set_on_restart()
    {
        BlockTreeTestScenario.ScenarioBuilder scenario = BlockTreeTestScenario.GoesLikeThis()
            .WithBlockTrees(10, 20)
            .InsertBeaconPivot(14)
            .Restart()
            .AssertBestKnownNumber(9)
            .AssertBestSuggestedHeader(9)
            .AssertBestSuggestedBody(9);
    }
    
    
    
}
